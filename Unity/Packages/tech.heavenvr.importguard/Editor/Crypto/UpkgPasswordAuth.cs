using System;
using System.IO;
using System.Security.Cryptography;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Password-derived key, PBKDF2-HMACSHA256. This is a distribution gate, not
    /// DRM (see UpkgCrypto's doc comment) - the point is stopping a package from
    /// silently opening for anyone who has the file, not defeating someone willing
    /// to brute-force it, so there is no benefit to anything heavier than PBKDF2
    /// here. Iteration count is stored per file (see WriteHeaderAndDeriveKey), so
    /// changing this constant only affects packages encrypted from now on -
    /// existing ones keep decrypting at whatever count they were written with.
    /// </summary>
    public sealed class UpkgPasswordAuth : IUpkgAuthMethod
    {
        public const byte Id = 1;
        public byte TypeId { get { return Id; } }
        public string DisplayName { get { return "Password"; } }

        const int SaltSize = 16;
        // Security-grade PBKDF2 guidance (OWASP: 600k+ for SHA256) targets secrets
        // worth defending against sustained offline brute-forcing. This only needs
        // to survive "double-click and it just opens" - see the class doc comment -
        // so it stays low enough that encrypting/opening a package doesn't stall
        // the Editor, especially under Mono's slower managed crypto vs. modern .NET.
        const int Iterations = 50000;
        const int KeyMaterialSize = 64; // 32 AES key + 32 HMAC key

        public byte[] WriteHeaderAndDeriveKey(BinaryWriter header, object credential, Action<float> onProgress)
        {
            var password = (string)credential;
            var salt = RandomBytes(SaltSize);
            header.Write(salt);
            header.Write(Iterations);
            return Derive(password, salt, Iterations);
        }

        public byte[] ReadHeaderAndDeriveKey(BinaryReader header, object credential, Action<float> onProgress)
        {
            var password = (string)credential;
            var salt = header.ReadBytes(SaltSize);
            var iterations = header.ReadInt32();
            return Derive(password, salt, iterations);
        }

        static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(KeyMaterialSize);
        }

        static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        public object PromptForNewCredential(string packageName)
        {
            return UpkgPasswordPrompt.PromptNew(packageName);
        }

        public object PromptForExistingCredential(string packageName)
        {
            return UpkgPasswordPrompt.PromptExisting(packageName);
        }
    }
}
