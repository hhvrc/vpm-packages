using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Encrypts the raw tar UpkgTwoInOne builds for its hidden payload, so a
    /// locked-down export just needs to be handed to someone rather than escorted.
    /// This never touches the tar layer itself - encryption happens after
    /// ExportPackage has already produced the raw tar, and decryption produces a
    /// plain tar back before anything else reads it, so the rest of the tool stays
    /// unaware encryption exists.
    ///
    /// This is a distribution gate, not DRM: the ciphertext and the code that
    /// decrypts it both ship to whoever receives the file, so anyone determined
    /// enough can always get past it. It only needs to stop "double-click and it
    /// just opens" - see the CLAUDE.md note this feature was scoped against.
    ///
    /// The whole payload is encrypted, not just a prefix: a raw tar (unlike the
    /// gzip stream it used to be, before UpkgTwoInOne stopped nesting a second
    /// compression layer - see that class) has no sequential decode dependency -
    /// tar entries are independently block-aligned and individually parseable, so
    /// leaving any of it unencrypted would leak every file after that point in the
    /// clear to anyone who extracts the outer archive (which needs no password;
    /// gzip isn't encryption).
    ///
    /// Container layout: magic(8) | authType(1) | [authType's own header, e.g. a KDF
    /// salt] | iv(16) | ciphertext length(4) | ciphertext(N) | HMAC-SHA256 tag(32)
    /// over everything before it (encrypt-then-MAC). AES-256-CBC rather than
    /// AES-GCM: broader Mono/.NET support across the editor platforms this runs on.
    /// </summary>
    public static class UpkgCrypto
    {
        static readonly byte[] Magic = { (byte)'H', (byte)'V', (byte)'R', (byte)'E', (byte)'N', (byte)'C', 0x01, 0 };
        const int TagSize = 32;

        // How many AES blocks are transformed between progress callbacks - about
        // 1 MB per tick, frequent enough to look smooth without spending more time
        // in progress-bar UI calls than in the actual crypto.
        const int ProgressChunkBlocks = 64 * 1024;

        /// <summary>Every authentication method this build understands. Add new ones
        /// here - existing encrypted files keep working, since the header only
        /// stores a type id.</summary>
        public static readonly IUpkgAuthMethod[] Methods = { new UpkgPasswordAuth() };

        public static IUpkgAuthMethod FindMethod(byte typeId)
        {
            foreach (var m in Methods) if (m.TypeId == typeId) return m;
            return null;
        }

        /// <summary>Peeks the auth type id without decrypting anything, so a caller
        /// can fail fast (unknown type) before ever asking for a credential.
        /// <paramref name="container"/> is the bytes of a hidden tar entry (see
        /// UpkgTwoInOne), not a whole file.</summary>
        public static byte PeekAuthType(byte[] container)
        {
            using (var ms = new MemoryStream(container, false))
            using (var br = new BinaryReader(ms))
            {
                br.ReadBytes(Magic.Length);
                return br.ReadByte();
            }
        }

        /// <summary>Encrypts an already-exported raw tar into a new container file.
        /// <paramref name="onProgress"/>, if given, is called with a short label
        /// and a 0..1 fraction as each stage proceeds - key derivation and the AES
        /// pass are each slow enough on a large package to need their own
        /// incremental progress, not one shared guess.</summary>
        public static void Encrypt(string plainPackagePath, string outputPath,
                                   IUpkgAuthMethod method, object credential,
                                   Action<string, float> onProgress = null)
        {
            var plaintext = File.ReadAllBytes(plainPackagePath);

            using (var ms = new MemoryStream())
            {
                byte[] aesKey, hmacKey;
                using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    bw.Write(Magic);
                    bw.Write(method.TypeId);

                    var kdfWatch = Stopwatch.StartNew();
                    var keyMaterial = method.WriteHeaderAndDeriveKey(bw, credential,
                        f => Report(onProgress, "Deriving key...", f));
                    kdfWatch.Stop();
                    Split(keyMaterial, out aesKey, out hmacKey);

                    var iv = RandomBytes(16);
                    var aesWatch = Stopwatch.StartNew();
                    byte[] ciphertext;
                    using (var aes = Aes.Create())
                    {
                        aes.KeySize = 256;
                        aes.Key = aesKey;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        using (var enc = aes.CreateEncryptor())
                            ciphertext = TransformChunked(enc, plaintext,
                                f => Report(onProgress, "Encrypting...", f));
                    }
                    aesWatch.Stop();
                    Debug.Log($"[Import Guard] encrypt: key derivation {kdfWatch.ElapsedMilliseconds}ms, AES-256-CBC over {plaintext.Length / (1024 * 1024)} MB {aesWatch.ElapsedMilliseconds}ms");

                    bw.Write(iv);
                    bw.Write(ciphertext.Length);
                    bw.Write(ciphertext);
                }

                var body = ms.ToArray();
                var tag = Hmac(hmacKey, body, 0, body.Length);
                Array.Clear(aesKey, 0, aesKey.Length);
                Array.Clear(hmacKey, 0, hmacKey.Length);

                using (var outFile = File.Create(outputPath))
                {
                    outFile.Write(body, 0, body.Length);
                    outFile.Write(tag, 0, tag.Length);
                }
            }
        }

        /// <summary>
        /// Decrypts a container - the bytes of a hidden tar entry (see
        /// UpkgTwoInOne), not a whole file - into a fresh temp raw tar and returns
        /// its path. The caller is responsible for deleting it once done. Throws
        /// <see cref="UpkgCryptoWrongCredentialException"/> for a wrong password
        /// (or corrupt data - the two are indistinguishable from a failed integrity
        /// check) and <see cref="UpkgCryptoException"/> for anything else,
        /// including an auth type this build doesn't know.
        /// </summary>
        public static string DecryptToTemp(byte[] all, object credential,
                                           Action<string, float> onProgress = null)
        {
            if (all.Length < Magic.Length + 1)
                throw new UpkgCryptoException("That file is too small to be an encrypted package.");

            for (int i = 0; i < Magic.Length; i++)
                if (all[i] != Magic[i])
                    throw new UpkgCryptoException("Not a HeavenVR encrypted package.");

            byte[] aesKey, hmacKey;
            byte[] iv;
            int ctOffset, ctLen, tagOffset;

            using (var ms = new MemoryStream(all, 0, all.Length, writable: false))
            using (var br = new BinaryReader(ms))
            {
                br.ReadBytes(Magic.Length);
                byte typeId = br.ReadByte();
                var method = FindMethod(typeId);
                if (method == null)
                    throw new UpkgCryptoException(
                        $"This package uses an authentication type ({typeId}) this version of Import Guard doesn't support. Update the package.");

                var kdfWatch = Stopwatch.StartNew();
                byte[] keyMaterial;
                try
                {
                    keyMaterial = method.ReadHeaderAndDeriveKey(br, credential,
                        f => Report(onProgress, "Deriving key...", f));
                }
                catch (Exception ex) { throw new UpkgCryptoException($"Corrupt package header: {ex.Message}"); }
                kdfWatch.Stop();
                Split(keyMaterial, out aesKey, out hmacKey);

                iv = br.ReadBytes(16);
                ctLen = br.ReadInt32();
                ctOffset = (int)ms.Position;

                if (ctOffset < 0 || ctLen < 0 || (long)ctOffset + ctLen + TagSize > all.Length)
                {
                    Array.Clear(hmacKey, 0, hmacKey.Length);
                    Array.Clear(aesKey, 0, aesKey.Length);
                    throw new UpkgCryptoException("Corrupt package: ciphertext length out of range.");
                }
                tagOffset = ctOffset + ctLen;

                var expectedTag = Hmac(hmacKey, all, 0, tagOffset);
                var tag = new byte[TagSize];
                Array.Copy(all, tagOffset, tag, 0, TagSize);
                bool ok = FixedTimeEquals(expectedTag, tag);
                Array.Clear(hmacKey, 0, hmacKey.Length);

                if (!ok)
                {
                    Array.Clear(aesKey, 0, aesKey.Length);
                    throw new UpkgCryptoWrongCredentialException("Wrong password, or the file is corrupt.");
                }

                Debug.Log($"[Import Guard] decrypt: key derivation {kdfWatch.ElapsedMilliseconds}ms");
            }

            var aesWatch = Stopwatch.StartNew();
            byte[] plaintext;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Key = aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var dec = aes.CreateDecryptor())
                    plaintext = TransformChunked(dec, all, ctOffset, ctLen,
                        f => Report(onProgress, "Decrypting...", f));
            }
            aesWatch.Stop();
            Array.Clear(aesKey, 0, aesKey.Length);
            Debug.Log($"[Import Guard] decrypt: AES-256-CBC over {plaintext.Length / (1024 * 1024)} MB {aesWatch.ElapsedMilliseconds}ms");

            var tempPath = Path.Combine(Path.GetTempPath(), $"importguard_{Guid.NewGuid():N}.unitypackage");
            File.WriteAllBytes(tempPath, plaintext);
            return tempPath;
        }

        static void Report(Action<string, float> onProgress, string phase, float fraction)
        {
            if (onProgress != null) onProgress(phase, fraction);
        }

        static byte[] TransformChunked(ICryptoTransform transform, byte[] data, Action<float> onProgress)
        {
            return TransformChunked(transform, data, 0, data.Length, onProgress);
        }

        /// <summary>
        /// TransformFinalBlock alone gives no way to report progress mid-call, so
        /// everything but the last (possibly padded/short) block goes through
        /// TransformBlock in fixed-size chunks instead - functionally identical
        /// for CBC, since it processes one 16-byte block at a time internally
        /// either way, just with a callback between chunks.
        /// </summary>
        static byte[] TransformChunked(ICryptoTransform transform, byte[] data, int offset, int count,
                                       Action<float> onProgress)
        {
            int blockSize = transform.InputBlockSize;
            int chunkBytes = blockSize * ProgressChunkBlocks;

            using (var output = new MemoryStream(count + blockSize))
            {
                int done = 0;
                var buf = new byte[chunkBytes];
                while (count - done > chunkBytes)
                {
                    int n = transform.TransformBlock(data, offset + done, chunkBytes, buf, 0);
                    output.Write(buf, 0, n);
                    done += chunkBytes;
                    if (onProgress != null) onProgress((float)done / count);
                }

                var tail = transform.TransformFinalBlock(data, offset + done, count - done);
                output.Write(tail, 0, tail.Length);
                if (onProgress != null) onProgress(1f);
                return output.ToArray();
            }
        }

        static void Split(byte[] material, out byte[] aesKey, out byte[] hmacKey)
        {
            aesKey = new byte[32];
            hmacKey = new byte[32];
            Array.Copy(material, 0, aesKey, 0, 32);
            Array.Copy(material, 32, hmacKey, 0, 32);
        }

        static byte[] Hmac(byte[] key, byte[] data, int offset, int length)
        {
            using (var h = new HMACSHA256(key))
                return h.ComputeHash(data, offset, length);
        }

        static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    public class UpkgCryptoException : Exception
    {
        public UpkgCryptoException(string message) : base(message) { }
    }

    public sealed class UpkgCryptoWrongCredentialException : UpkgCryptoException
    {
        public UpkgCryptoWrongCredentialException(string message) : base(message) { }
    }
}
