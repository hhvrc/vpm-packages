using System;
using System.Collections.Generic;
using UnityEditor;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// guid &lt;-&gt; path index for the open project, including Packages/ (a VPM
    /// package owns guids too, and a .unitypackage shipping its own copy of one
    /// will collide with them).
    ///
    /// Built from AssetDatabase, so it is effectively instant compared to
    /// walking every .meta file on disk.
    /// </summary>
    public class UpkgProject
    {
        public readonly Dictionary<string, string> GuidToPath =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> PathToGuid =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public int Count { get { return GuidToPath.Count; } }

        public static UpkgProject Build()
        {
            var index = new UpkgProject();
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) &&
                    !path.StartsWith("Packages/", StringComparison.Ordinal))
                    continue;

                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;

                guid = guid.ToLowerInvariant();
                if (!index.GuidToPath.ContainsKey(guid))
                    index.GuidToPath.Add(guid, path);

                var key = UpkgText.Normalize(path);
                if (!index.PathToGuid.ContainsKey(key))
                    index.PathToGuid.Add(key, guid);
            }
            return index;
        }

        public bool HasGuid(string guid)
        {
            return guid != null && GuidToPath.ContainsKey(guid);
        }

        public string PathOfGuid(string guid)
        {
            string path;
            return guid != null && GuidToPath.TryGetValue(guid, out path) ? path : null;
        }

        public string GuidAtPath(string path)
        {
            string guid;
            return PathToGuid.TryGetValue(UpkgText.Normalize(path), out guid) ? guid : null;
        }
    }
}
