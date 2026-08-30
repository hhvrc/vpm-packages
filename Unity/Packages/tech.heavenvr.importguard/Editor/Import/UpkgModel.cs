using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HeavenVR.ImportGuard
{
    /// <summary>One asset inside a package, identified by its folder guid.</summary>
    public class UpkgEntry
    {
        public string Guid;
        public string PathName = "";
        public string PathNameExtra = "";
        public byte[] Meta;
        public bool HasAsset;
        public long AssetSize;
        public bool HasPreview;

        public bool IsFolder { get { return !HasAsset; } }

        public string Extension
        {
            get
            {
                var ext = Path.GetExtension(PathName);
                return ext == null ? "" : ext.ToLowerInvariant();
            }
        }

        /// <summary>Top-level grouping key, e.g. "Assets/GoGo".</summary>
        public string Group
        {
            get
            {
                var parts = PathName.Split('/');
                return parts.Length > 1 ? $"{parts[0]}/{parts[1]}" : PathName;
            }
        }
    }

    /// <summary>What importing an entry would do to the project.</summary>
    public enum UpkgVerdict
    {
        /// <summary>No conflict.</summary>
        New,
        /// <summary>Same path and guid: an ordinary clean update.</summary>
        Update,
        /// <summary>Two entries inside the package target the same path.</summary>
        Duplicate,
        /// <summary>An asset already lives at this path with a different guid;
        /// the file is replaced and references to it break.</summary>
        PathHijack,
        /// <summary>The package guid already belongs to a DIFFERENT project asset.
        /// Two files end up claiming one guid. Unity shows no import warning.</summary>
        GuidStolen,
    }

    /// <summary>What the user decided to do with an entry.</summary>
    public enum UpkgAction
    {
        /// <summary>Import, minting a fresh guid if it would collide.</summary>
        Import,
        /// <summary>Import keeping the original guid, collision or not.</summary>
        ImportKeepGuid,
        /// <summary>Leave it out; the project's copy (if any) wins.</summary>
        Skip,
    }

    public class UpkgRow
    {
        public UpkgEntry Entry;
        public UpkgVerdict Verdict;
        public string ProjectPath;    // the colliding project asset, if any
        public string ProjectGuid;

        public UpkgAction Action = UpkgAction.Import;
        public string NewGuid;        // assigned during planning
        public string RedirectTo;     // project guid that references should point at

        public bool IsConflict
        {
            get
            {
                return Verdict == UpkgVerdict.GuidStolen ||
                       Verdict == UpkgVerdict.PathHijack ||
                       Verdict == UpkgVerdict.Duplicate;
            }
        }
    }

    public static class UpkgText
    {
        public static readonly Regex GuidPattern =
            new Regex("\\b[0-9a-f]{32}\\b", RegexOptions.Compiled);

        static readonly Regex MetaGuid =
            new Regex("^guid:\\s*([0-9a-fA-F]{32})", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Extensions whose payload is Unity-serialised text carrying guid references.</summary>
        public static readonly HashSet<string> TextAssetExtensions = new HashSet<string>
        {
            ".prefab", ".unity", ".asset", ".mat", ".controller", ".overridecontroller",
            ".anim", ".mask", ".physicmaterial", ".physicsmaterial2d", ".preset",
            ".playable", ".shadervariants", ".spriteatlas", ".terrainlayer",
            ".fontsettings", ".guiskin", ".flare", ".cubemap", ".mixer", ".signal",
            ".lighting", ".rendertexture", ".giparams", ".brush", ".shadergraph",
            ".shadersubgraph", ".vfx", ".inputactions", ".asmdef", ".asmref",
        };

        public static string ReadMetaGuid(byte[] meta)
        {
            if (meta == null) return null;
            var text = Encoding.UTF8.GetString(meta, 0, Math.Min(meta.Length, 512));
            var m = MetaGuid.Match(text);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        public static string Normalize(string path)
        {
            if (path == null) return "";
            return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }

        /// <summary>
        /// Rewrites guid references in place. Replacements are the same length as
        /// the originals, so byte offsets inside binary payloads are preserved.
        /// </summary>
        public static byte[] Substitute(byte[] data, Dictionary<string, string> map, out int hits)
        {
            hits = 0;
            if (data == null || data.Length < 32 || map.Count == 0) return data;

            // Latin1 round-trips every byte value, so binary payloads survive intact.
            var enc = Encoding.GetEncoding(28591);
            var text = enc.GetString(data);
            int found = 0;
            var result = GuidPattern.Replace(text, m =>
            {
                string replacement;
                if (map.TryGetValue(m.Value, out replacement))
                {
                    found++;
                    return replacement;
                }
                return m.Value;
            });
            hits = found;
            return found == 0 ? data : enc.GetBytes(result);
        }

        // Fixed, deliberately never configurable. Deriving a new guid from nothing
        // but the original guid means the same source asset always lands on the same
        // new guid - so importing v1.1 of a package over v1.0 keeps every reference
        // you already have, with nothing for anyone to remember or write down.
        const string MintNamespace = "HeavenVR.PackageGuard/1";

        /// <summary>
        /// Deterministic fresh guid for an asset whose original guid is already taken.
        /// </summary>
        public static string MintGuid(string sourceGuid, HashSet<string> taken)
        {
            for (int salt = 0; ; salt++)
            {
                string candidate;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var bytes = Encoding.UTF8.GetBytes($"{MintNamespace}|{sourceGuid}|{salt}");
                    var hash = md5.ComputeHash(bytes);
                    var sb = new StringBuilder(32);
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    candidate = sb.ToString();
                }
                if (!taken.Contains(candidate))
                {
                    taken.Add(candidate);
                    return candidate;
                }
            }
        }
    }
}
