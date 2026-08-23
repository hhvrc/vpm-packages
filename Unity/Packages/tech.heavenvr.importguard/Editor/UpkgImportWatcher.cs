using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Gets Import Guard in front of a .unitypackage import where Unity allows it,
    /// and reports the damage where it does not.
    ///
    /// Unity exposes no *supported* hook that can veto a package import:
    /// importPackageStarted is a notification with no return value, and by the time
    /// an AssetPostprocessor runs the guids have already been claimed.
    /// UpkgImportPatch gets a real veto by patching Unity's internal dialog, but
    /// that is an internal method and may not hold on every version, so this class
    /// remains as the layer that always works:
    ///
    ///   1. Double-clicking a .unitypackage inside the project opens Import Guard
    ///      instead of Unity's importer. OnOpenAsset can return true to consume the
    ///      event, which is a supported veto for that entry point.
    ///
    ///   2. If an import runs anyway - the patch is unavailable, or something else
    ///      called ImportPackage directly - the guid map is snapshotted when it
    ///      starts and compared when it finishes. That cannot prevent a silent
    ///      re-point, but it means you find out the moment it happens.
    ///
    /// Yes, this class is [InitializeOnLoad] - the exact pattern the script audit
    /// flags as high severity. That flag is correct: code that runs by itself is
    /// worth knowing about, including this code. It is in your repository, in
    /// source, and does nothing but read the asset database and print warnings.
    /// </summary>
    [InitializeOnLoad]
    public static class UpkgImportWatcher
    {
        static Dictionary<string, string> _before;
        static string _pending;

        static UpkgImportWatcher()
        {
            AssetDatabase.importPackageStarted += OnStarted;
            AssetDatabase.importPackageCompleted += OnCompleted;
            AssetDatabase.importPackageCancelled += OnFinishedWithoutImport;
            AssetDatabase.importPackageFailed += (name, error) => OnFinishedWithoutImport(name);
        }

        // ---- entry point we can actually intercept -------------------

        /// <summary>
        /// Consumes a double-click on a .unitypackage in the Project window and
        /// opens Import Guard instead. Returning true stops Unity's own handling.
        /// </summary>
        [OnOpenAsset(-1)]
        static bool OnOpenPackage(int instanceID, int line)
        {
            var path = AssetDatabase.GetAssetPath(instanceID);
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                return false;

            var full = Path.GetFullPath(path);
            UpkgWindow.OpenWith(full);
            return true;    // handled: Unity's importer never sees it
        }

        // ---- everything else: snapshot, then report ------------------

        static void OnStarted(string packageName)
        {
            // The import dialog is open but nothing has been written yet, so this
            // is the last moment the project's guids are still untouched.
            _pending = packageName;
            _before = Snapshot();
        }

        static void OnFinishedWithoutImport(string packageName)
        {
            _before = null;
            _pending = null;
        }

        static Dictionary<string, string> Snapshot()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) &&
                    !path.StartsWith("Packages/", StringComparison.Ordinal))
                    continue;

                var guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid)) map[guid.ToLowerInvariant()] = path;
            }
            return map;
        }

        static void OnCompleted(string packageName)
        {
            var before = _before;
            _before = null;
            _pending = null;
            if (before == null) return;

            var after = Snapshot();

            // A guid that now resolves somewhere else is the silent re-point this
            // whole tool exists to catch.
            var moved = new List<string>();
            foreach (var pair in before)
            {
                string nowAt;
                if (!after.TryGetValue(pair.Key, out nowAt)) continue;
                if (string.Equals(nowAt, pair.Value, StringComparison.OrdinalIgnoreCase))
                    continue;
                moved.Add(pair.Value + "\n        is now  " + nowAt);
            }

            // Code that arrived without anyone being asked about it. Membership goes
            // through a set: Dictionary.ContainsValue is a linear scan, and this runs
            // once per asset in a project with tens of thousands of them.
            var knownPaths = new HashSet<string>(before.Values, StringComparer.OrdinalIgnoreCase);
            var code = new List<string>();
            foreach (var path in after.Values)
            {
                if (knownPaths.Contains(path)) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (UpkgScriptAudit.CodeExtensions.Contains(ext)) code.Add(path);
            }

            if (moved.Count == 0 && code.Count == 0) return;

            var report = new System.Text.StringBuilder();
            report.Append("[Import Guard] after importing \"").Append(packageName).Append("\":");

            if (moved.Count > 0)
            {
                report.Append("\n\n  ").Append(moved.Count)
                      .Append(" of your assets had their guid taken over. Anything that "
                            + "referenced them now points somewhere else:");
                foreach (var line in moved.Take(15)) report.Append("\n    ").Append(line);
                if (moved.Count > 15)
                    report.Append("\n    ... and ").Append(moved.Count - 15).Append(" more");
            }

            if (code.Count > 0)
            {
                report.Append("\n\n  ").Append(code.Count)
                      .Append(" code file(s) were imported and will compile:");
                foreach (var path in code.Take(15)) report.Append("\n    ").Append(path);
                if (code.Count > 15)
                    report.Append("\n    ... and ").Append(code.Count - 15).Append(" more");
            }

            if (moved.Count > 0) Debug.LogError(report.ToString());
            else Debug.LogWarning(report.ToString());

            var summary = moved.Count > 0
                ? string.Format(
                    "{0} of your existing assets just had their guid taken over by this " +
                    "package. References to them now resolve somewhere else.{1}\n\n" +
                    "Undo this with version control if you can - the details are in the " +
                    "Console.\n\nNext time, double-click the package inside the Project " +
                    "window, or use Tools > HeavenVR > Package Import Guard, to review it " +
                    "before anything is written.",
                    moved.Count,
                    code.Count > 0 ? " " + code.Count + " code file(s) were also imported." : "")
                : string.Format(
                    "{0} code file(s) were imported and will compile. Unity does not ask " +
                    "before running code that comes in with a package.\n\nThe list is in " +
                    "the Console. Import Guard can show you what code does before it " +
                    "lands - open a package with it rather than with Unity's importer.",
                    code.Count);

            EditorUtility.DisplayDialog("Import Guard", summary, "OK");
        }
    }
}
