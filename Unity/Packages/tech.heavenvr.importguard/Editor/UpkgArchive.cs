using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Streaming reader/writer for the tar.gz container behind a .unitypackage.
    ///
    /// Members are grouped per asset:
    ///   {guid}/pathname     required, "Assets/Foo/Bar.fbx"
    ///   {guid}/asset        payload; absent for folders
    ///   {guid}/asset.meta   the .meta sidecar (carries `guid:`)
    ///   {guid}/preview.png  optional thumbnail
    ///
    /// Knows nothing about Unity projects or collisions - it only turns the
    /// archive into records and back again.
    /// </summary>
    public static class UpkgArchive
    {
        const int BlockSize = 512;

        public struct Member
        {
            public string Guid;
            public string Name;   // "pathname", "asset", "asset.meta", "preview.png"
            public long Size;
        }

        /// <summary>
        /// Walks every member in order. <paramref name="want"/> returns true to have
        /// the payload read and handed to <paramref name="onPayload"/>, false to skip
        /// it cheaply. <paramref name="onProgress"/> returns false to cancel.
        /// </summary>
        public static void Read(string path, Func<Member, bool> want,
                                Action<Member, byte[]> onPayload,
                                Func<long, long, bool> onProgress = null)
        {
            using (var file = File.OpenRead(path))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            {
                var header = new byte[BlockSize];
                long counter = 0;
                long total = file.Length;

                while (true)
                {
                    if (!ReadExactly(gz, header, BlockSize)) break;
                    if (IsAllZero(header)) break;               // end-of-archive marker

                    string name = ReadString(header, 0, 100);
                    long size = ReadOctal(header, 124, 12);
                    char type = (char)header[156];

                    if (type == 'L' || type == 'K')
                    {
                        // GNU long name: the real name arrives as the next payload.
                        var nameBytes = ReadPayload(gz, size);
                        name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                        if (!ReadExactly(gz, header, BlockSize)) break;
                        size = ReadOctal(header, 124, 12);
                        type = (char)header[156];
                    }

                    bool isFile = type == '0' || type == '\0';
                    var member = Split(name, size);

                    if (isFile && member.Guid != null && want(member))
                        onPayload(member, ReadPayload(gz, size));
                    else
                        SkipPayload(gz, size);

                    counter++;
                    if (onProgress != null && (counter & 0xFF) == 0 &&
                        !onProgress(file.Position, total))
                        return;   // caller cancelled
                }
            }
        }

        static Member Split(string name, long size)
        {
            name = name.Replace('\\', '/');
            int slash = name.IndexOf('/');
            if (slash <= 0 || slash == name.Length - 1)
                return new Member { Guid = null, Name = name, Size = size };
            return new Member
            {
                Guid = name.Substring(0, slash),
                Name = name.Substring(slash + 1),
                Size = size,
            };
        }

        // ---- writing -------------------------------------------------

        /// <summary>Writes a .unitypackage. Members must be added grouped by guid.</summary>
        public sealed class Writer : IDisposable
        {
            readonly FileStream _file;
            readonly GZipStream _gz;

            public Writer(string path)
            {
                _file = File.Create(path);
                _gz = new GZipStream(_file, CompressionLevel.Optimal);
            }

            public void Add(string guid, string name, byte[] data)
            {
                WriteHeader(_gz, guid + "/" + name, data.Length);
                _gz.Write(data, 0, data.Length);
                int pad = (int)((BlockSize - (data.Length % BlockSize)) % BlockSize);
                if (pad > 0) _gz.Write(new byte[pad], 0, pad);
            }

            public void Dispose()
            {
                _gz.Write(new byte[BlockSize * 2], 0, BlockSize * 2);  // end marker
                _gz.Dispose();
                _file.Dispose();
            }
        }

        static void WriteHeader(Stream to, string name, long size)
        {
            var h = new byte[BlockSize];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 100)
                throw new IOException("tar entry name too long: " + name);
            Array.Copy(nameBytes, h, nameBytes.Length);

            WriteOctal(h, 100, 8, 0x1A4);      // mode 0644
            WriteOctal(h, 108, 8, 0);          // uid
            WriteOctal(h, 116, 8, 0);          // gid
            WriteOctal(h, 124, 12, size);
            WriteOctal(h, 136, 12, 0);         // mtime
            h[156] = (byte)'0';                // regular file
            Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(h, 257);

            for (int i = 148; i < 156; i++) h[i] = (byte)' ';   // checksum field blank
            long sum = 0;
            foreach (var b in h) sum += b;
            WriteOctal(h, 148, 7, sum);
            h[155] = (byte)' ';

            to.Write(h, 0, BlockSize);
        }

        // ---- primitives ----------------------------------------------

        static bool ReadExactly(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        static byte[] ReadPayload(Stream s, long size)
        {
            var data = new byte[size];
            long read = 0;
            while (read < size)
            {
                int chunk = (int)Math.Min(int.MaxValue, size - read);
                int n = s.Read(data, (int)read, chunk);
                if (n <= 0) throw new EndOfStreamException("truncated tar payload");
                read += n;
            }
            SkipPadding(s, size);
            return data;
        }

        static void SkipPayload(Stream s, long size)
        {
            var scratch = new byte[16384];
            long left = size;
            while (left > 0)
            {
                int n = s.Read(scratch, 0, (int)Math.Min(scratch.Length, left));
                if (n <= 0) throw new EndOfStreamException("truncated tar payload");
                left -= n;
            }
            SkipPadding(s, size);
        }

        static void SkipPadding(Stream s, long size)
        {
            int pad = (int)((BlockSize - (size % BlockSize)) % BlockSize);
            if (pad == 0) return;
            var scratch = new byte[pad];
            ReadExactly(s, scratch, pad);
        }

        static bool IsAllZero(byte[] b)
        {
            foreach (var x in b) if (x != 0) return false;
            return true;
        }

        static string ReadString(byte[] b, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && b[end] != 0) end++;
            return Encoding.UTF8.GetString(b, offset, end - offset);
        }

        static long ReadOctal(byte[] b, int offset, int length)
        {
            long value = 0;
            for (int i = offset; i < offset + length; i++)
            {
                if (b[i] == 0 || b[i] == ' ')
                {
                    if (value != 0) break;
                    continue;
                }
                if (b[i] < '0' || b[i] > '7') break;
                value = value * 8 + (b[i] - '0');
            }
            return value;
        }

        static void WriteOctal(byte[] b, int offset, int length, long value)
        {
            var text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
            var bytes = Encoding.ASCII.GetBytes(text);
            Array.Copy(bytes, 0, b, offset, Math.Min(bytes.Length, length - 1));
        }
    }
}
