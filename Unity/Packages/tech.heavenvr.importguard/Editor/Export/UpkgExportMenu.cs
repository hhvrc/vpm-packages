using System.Linq;
using UnityEditor;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Entry point only - reached the same way Unity's own Export Package is,
    /// right-click selected assets, so nobody has to find Tools > HeavenVR to
    /// protect what they just made. See UpkgExportWindow for the actual tree/build.
    /// </summary>
    public static class UpkgExportMenu
    {
        [MenuItem("Assets/Export Protected Package...", false, 20)]
        static void Export()
        {
            var guids = Selection.assetGUIDs;
            if (guids == null || guids.Length == 0) return;

            var roots = guids.Select(AssetDatabase.GUIDToAssetPath)
                             .Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (roots.Length == 0) return;

            UpkgExportWindow.Open(roots);
        }

        [MenuItem("Assets/Export Protected Package...", true)]
        static bool Validate()
        {
            return Selection.assetGUIDs != null && Selection.assetGUIDs.Length > 0;
        }
    }
}
