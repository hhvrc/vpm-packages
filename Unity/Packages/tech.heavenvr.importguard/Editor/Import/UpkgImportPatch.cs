using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Puts Import Guard in front of Unity's own import dialog.
    ///
    /// Unity has no supported veto hook, but every interactive package import -
    /// double-clicking a .unitypackage in Explorer, dragging one into the project,
    /// Assets > Import Package > Custom Package - funnels through one internal
    /// managed method:
    ///
    ///     UnityEditor.PackageImport.ShowImportPackage(
    ///         string packagePath, ImportPackageItem[] items, string packageIconPath)
    ///
    /// A Harmony prefix returning false suppresses that dialog, and the first
    /// argument is the full path of the package - exactly what Import Guard needs.
    ///
    /// This patches an internal Unity method, which is not a contract Unity keeps.
    /// So everything here is deliberately defensive:
    ///
    ///   - Harmony is reached by reflection, not a compile-time reference, so the
    ///     tool still builds in a project that does not ship it. (VRChat projects
    ///     always do: the SDK includes it.)
    ///   - If the method, Harmony, or the patch is missing or throws, nothing is
    ///     patched and the tool falls back to the OnOpenAsset interception and the
    ///     after-the-fact report in UpkgImportWatcher.
    ///   - If the prefix itself ever throws, it returns true so Unity's importer
    ///     runs as normal. Failing to guard an import is bad; making it impossible
    ///     to import anything at all would be far worse.
    /// </summary>
    [InitializeOnLoad]
    public static class UpkgImportPatch
    {
        const string HarmonyId = "dev.heavenvr.packageguard";

        /// <summary>Set while deliberately handing a package back to Unity's importer.</summary>
        static bool _bypassOnce;

        public static bool Installed { get; private set; }
        public static string Unavailable { get; private set; }

        static UpkgImportPatch()
        {
            // Deferred: patching during static construction can race the editor's
            // own assembly setup, and there is nothing to guard until the UI exists.
            EditorApplication.delayCall += TryInstall;
        }

        static void TryInstall()
        {
            if (Installed) return;
            try
            {
                var target = FindShowImportPackage();
                if (target == null)
                {
                    Unavailable = "UnityEditor.PackageImport.ShowImportPackage was not found on this Unity version";
                    return;
                }

                var harmonyType = FindType("HarmonyLib.Harmony");
                var harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
                if (harmonyType == null || harmonyMethodType == null)
                {
                    Unavailable = "Harmony is not present in this project";
                    return;
                }

                var prefix = typeof(UpkgImportPatch).GetMethod(
                    nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static);

                var harmony = Activator.CreateInstance(harmonyType, HarmonyId);
                var harmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);

                var patch = harmonyType.GetMethod("Patch");
                if (patch == null)
                {
                    Unavailable = "Harmony.Patch was not found";
                    return;
                }

                // Fill by position, then override the prefix slot: the trailing
                // parameters have varied across Harmony versions.
                var parameters = patch.GetParameters();
                var arguments = new object[parameters.Length];
                arguments[0] = target;
                for (int i = 1; i < arguments.Length; i++) arguments[i] = null;

                int prefixIndex = Array.FindIndex(parameters, p => p.Name == "prefix");
                if (prefixIndex < 0) prefixIndex = 1;
                arguments[prefixIndex] = harmonyMethod;

                patch.Invoke(harmony, arguments);
                Installed = true;
            }
            catch (Exception ex)
            {
                Unavailable = ex.GetBaseException().Message;
                Debug.LogWarning($"[Import Guard] could not take over Unity's import dialog: {Unavailable}\nDouble-clicking a package inside the Project window still opens Import Guard, and imports are still checked afterwards.");
            }
        }

        static MethodInfo FindShowImportPackage()
        {
            // PackageImport sits alongside EditorWindow in both UnityEditor.dll and
            // UnityEditor.CoreModule.dll, but which of those is loaded has moved
            // between versions, so fall back to a scan rather than assuming.
            var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.PackageImport")
                       ?? FindType("UnityEditor.PackageImport");
            if (type == null) return null;

            // Matched by name and first parameter rather than an exact signature,
            // because the trailing arguments have changed between Unity versions.
            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static)
                       .FirstOrDefault(m => m.Name == "ShowImportPackage" &&
                                            m.GetParameters().Length > 0 &&
                                            m.GetParameters()[0].ParameterType == typeof(string) &&
                                            m.GetParameters()[0].Name == "packagePath");
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName, false); }
                catch { continue; }
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>
        /// Runs instead of Unity's import dialog. Returning false skips the original.
        /// Harmony binds `packagePath` by name, so the internal item array in the
        /// real signature never has to be named here.
        /// </summary>
        static bool Prefix(string packagePath)
        {
            try
            {
                if (_bypassOnce)
                {
                    _bypassOnce = false;
                    return true;         // deliberately handed back to Unity
                }

                if (string.IsNullOrEmpty(packagePath)) return true;

                UpkgWindow.OpenWith(packagePath);
                return false;            // Unity's dialog never appears
            }
            catch (Exception ex)
            {
                // Never leave the user unable to import anything.
                Debug.LogError($"[Import Guard] failed to take over this import, handing it back to Unity: {ex}");
                return true;
            }
        }

        /// <summary>
        /// Hands a package to Unity's own importer, once, without this patch
        /// catching it again.
        /// </summary>
        public static void ImportWithUnity(string packagePath)
        {
            _bypassOnce = true;
            try
            {
                AssetDatabase.ImportPackage(packagePath, true);
            }
            catch
            {
                _bypassOnce = false;
                throw;
            }
        }
    }
}
