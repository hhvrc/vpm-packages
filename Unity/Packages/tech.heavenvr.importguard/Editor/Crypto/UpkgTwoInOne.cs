using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// A "protected" export is one ordinary-looking .unitypackage: stock Unity can
    /// open it and sees only a small placeholder asset (a script whose import pops
    /// up "get Import Guard"). The real package sits inside as one extra tar entry
    /// under a name Unity's own importer doesn't recognize as an asset (see
    /// HiddenGuid/HiddenName) - not a guid, deliberately, so nothing in Unity's
    /// guid-keyed import machinery ever tries to treat it as one - holding an
    /// UpkgCrypto-encrypted container. This is a UX courtesy for someone who double-
    /// clicks the file without Import Guard installed, not a security layer on top
    /// of what the encryption already provides: anyone who knows to look (or just
    /// runs it through a normal tar reader) can see the hidden entry exists.
    /// </summary>
    public static class UpkgTwoInOne
    {
        public const string HiddenGuid = "__heavenvr_payload__";
        public const string HiddenName = "data";

        /// <summary>
        /// Builds a protected package at outputPath: a placeholder Unity can open
        /// normally, plus realPlainPackagePath encrypted and hidden inside it.
        /// </summary>
        public static void Write(string realPlainPackagePath, string outputPath,
                                 IUpkgAuthMethod method, object credential,
                                 string packageDisplayName, Action<string, float> onProgress = null)
        {
            var containerTemp = Path.Combine(Path.GetTempPath(), $"importguard_container_{Guid.NewGuid():N}");
            try
            {
                UpkgCrypto.Encrypt(realPlainPackagePath, containerTemp, method, credential, onProgress);
                var containerBytes = File.ReadAllBytes(containerTemp);

                if (onProgress != null) onProgress("Building placeholder...", 1f);
                // Fastest, not Optimal: containerBytes is ciphertext, indistinguishable
                // from noise - DEFLATE gains nothing searching it for matches, only
                // spends time finding none.
                using (var writer = new UpkgArchive.Writer(outputPath, CompressionLevel.Fastest))
                {
                    WritePlaceholder(writer, packageDisplayName);
                    writer.Add(HiddenGuid, HiddenName, containerBytes);
                }
            }
            finally
            {
                try { File.Delete(containerTemp); } catch { /* best-effort */ }
            }
        }

        /// <summary>Looks for the hidden entry without decrypting anything. Returns
        /// null if <paramref name="path"/> isn't a protected package (a normal
        /// package, or not a .unitypackage at all).</summary>
        public static byte[] TryReadHidden(string path)
        {
            byte[] found = null;
            try
            {
                UpkgArchive.Read(path,
                    want: m => m.Guid == HiddenGuid && m.Name == HiddenName,
                    onPayload: (m, data) => found = data);
            }
            catch
            {
                return null;
            }
            return found;
        }

        static void WritePlaceholder(UpkgArchive.Writer writer, string packageDisplayName)
        {
            var guid = Guid.NewGuid().ToString("N");
            var fingerprint = Guid.NewGuid().ToString("N").Substring(0, 8);
            var displayName = string.IsNullOrEmpty(packageDisplayName) ? "This package" : packageDisplayName;

            var pathname = $"Assets/HeavenVR-Protected/Notice_{fingerprint}.cs";
            var script = BuildNoticeScript(fingerprint, displayName);

            writer.Add(guid, "pathname", Encoding.UTF8.GetBytes(pathname));
            writer.Add(guid, "asset", Encoding.UTF8.GetBytes(script));
            writer.Add(guid, "asset.meta", Encoding.UTF8.GetBytes(BuildMeta(guid)));
        }

        // The namespace carries a per-export fingerprint so importing more than one
        // protected package into the same project can't collide on a duplicate type
        // name and fail the whole project's compile.
        //
        // packageDisplayName travels as Base64, not a quoted string literal: it
        // comes from a filename the user picked, which can contain characters
        // (newlines, unescaped quotes, anything) that would corrupt a hand-escaped
        // literal and break the generated script's own compile. Base64's alphabet
        // has nothing that needs escaping in C#, so there is no escaping logic to
        // get wrong.
        static string BuildNoticeScript(string fingerprint, string packageDisplayName)
        {
            var nameBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(packageDisplayName));
            return
                "using System;\n" +
                "using System.Text;\n" +
                "using UnityEditor;\n" +
                "using UnityEngine;\n" +
                "\n" +
                "namespace HeavenVRProtected_" + fingerprint + "\n" +
                "{\n" +
                "    [InitializeOnLoad]\n" +
                "    static class Notice\n" +
                "    {\n" +
                "        static Notice()\n" +
                "        {\n" +
                "            EditorApplication.delayCall += Show;\n" +
                "        }\n" +
                "\n" +
                "        static void Show()\n" +
                "        {\n" +
                "            var name = Encoding.UTF8.GetString(Convert.FromBase64String(\"" +
                                  nameBase64 + "\"));\n" +
                "            EditorUtility.DisplayDialog(\n" +
                "                name,\n" +
                "                \"What just imported is a placeholder - this package is protected, " +
                                  "and needs HeavenVR Import Guard to unlock its real contents.\\n\\n\" +\n" +
                "                \"Get it from https://vpm.heavenvr.tech, then re-open this " +
                                  ".unitypackage with it instead of Unity's own importer.\",\n" +
                "                \"OK\");\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
        }

        static string BuildMeta(string guid)
        {
            return
                "fileFormatVersion: 2\n" +
                "guid: " + guid + "\n" +
                "MonoImporter:\n" +
                "  externalObjects: {}\n" +
                "  serializedVersion: 2\n" +
                "  defaultReferences: []\n" +
                "  executionOrder: 0\n" +
                "  icon: {instanceID: 0}\n" +
                "  userData: \n" +
                "  assetBundleName: \n" +
                "  assetBundleVariant: \n";
        }
    }
}
