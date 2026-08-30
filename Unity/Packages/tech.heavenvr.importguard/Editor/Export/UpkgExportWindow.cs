using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Lets the user prune the export selection before it's protected and written -
    /// same TreeView/checkbox/stylesheet as UpkgWindow's import review (see
    /// UpkgWindow.uss), so this reads as the same tool rather than a bolted-on
    /// dialog. All / None / Include Dependencies mirror Unity's own Export
    /// Package window. A folder in the selection expands to everything inside it,
    /// same as dragging one into a build.
    /// </summary>
    public class UpkgExportWindow : EditorWindow
    {
        public static void Open(string[] selectedPaths)
        {
            var window = CreateInstance<UpkgExportWindow>();
            window.titleContent = new GUIContent("Export Protected Package");
            window.minSize = new Vector2(480, 420);
            window._selectedPaths = selectedPaths;
            window.Show();

            if (window._treeView != null) window.LoadSelection();
        }

        string[] _selectedPaths;
        string _displayName;
        UpkgExportTree.Node _tree;
        readonly List<UpkgExportTree.Node> _flat = new List<UpkgExportTree.Node>();
        readonly Dictionary<int, UpkgExportTree.Node> _byId = new Dictionary<int, UpkgExportTree.Node>();
        TreeView _treeView;
        Label _countPill;
        Toggle _includeDepsToggle;

        public void CreateGUI()
        {
            var uxml = LoadTree();
            if (uxml == null)
            {
                rootVisualElement.Add(new Label(
                    "UpkgExportWindow.uxml could not be found next to UpkgExportWindow.cs."));
                return;
            }
            uxml.CloneTree(rootVisualElement);

            _countPill = rootVisualElement.Q<Label>("count-pill");

            rootVisualElement.Q<Button>("select-all").clicked += () => SetAllAndRefresh(true);
            rootVisualElement.Q<Button>("select-none").clicked += () => SetAllAndRefresh(false);
            rootVisualElement.Q<Button>("export").clicked += DoExport;

            _includeDepsToggle = rootVisualElement.Q<Toggle>("include-deps");
            _includeDepsToggle.RegisterValueChangedCallback(e =>
            {
                // Turning it on applies to whatever's checked right now, same as
                // Unity's own dialog; turning it off doesn't retroactively remove
                // anything - it only stops future checks from cascading.
                if (e.newValue) IncludeDependencies();
            });

            BuildTreeView();
            if (_selectedPaths != null) LoadSelection();
        }

        /// <summary>Finds the layout next to this script, same trick UpkgWindow
        /// uses so the tool works wherever it lands in a project.</summary>
        static VisualTreeAsset LoadTree()
        {
            foreach (var guid in AssetDatabase.FindAssets("UpkgExportWindow t:VisualTreeAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("UpkgExportWindow.uxml", StringComparison.OrdinalIgnoreCase))
                    return AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            }
            return null;
        }

        void BuildTreeView()
        {
            var host = rootVisualElement.Q("tree-host");
            _treeView = new TreeView
            {
                fixedItemHeight = 20f,
                selectionType = SelectionType.None,
                makeItem = MakeTreeRow,
                bindItem = BindTreeRow,
            };
            _treeView.style.flexGrow = 1f;
            host.Add(_treeView);
        }

        void LoadSelection()
        {
            var files = ExpandToFiles(_selectedPaths).ToArray();
            var withDeps = files
                .Concat(AssetDatabase.GetDependencies(files, true))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _tree = UpkgExportTree.Build(withDeps);
            UpkgExportTree.Recount(_tree);
            _displayName = _selectedPaths.Length == 1
                ? Path.GetFileNameWithoutExtension(_selectedPaths[0])
                : "Package";

            RebuildTree();
        }

        /// <summary>A folder in the selection expands to every asset inside it,
        /// recursively - exporting a folder should include its subfolders, the
        /// way dragging one into a build (or Unity's own Export Package) does.</summary>
        static IEnumerable<string> ExpandToFiles(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths) Expand(path, set);
            return set;
        }

        static void Expand(string path, HashSet<string> set)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (AssetDatabase.IsValidFolder(path))
            {
                foreach (var guid in AssetDatabase.FindAssets("", new[] { path }))
                    Expand(AssetDatabase.GUIDToAssetPath(guid), set);
                return;
            }
            set.Add(path);
        }

        // ---- tree (mirrors UpkgWindow's RebuildTree/ToItem/MakeTreeRow/BindTreeRow) ----

        void RebuildTree()
        {
            _flat.Clear();
            _byId.Clear();
            var roots = new List<TreeViewItemData<int>>();
            foreach (var child in _tree.Children ?? new List<UpkgExportTree.Node>())
                roots.Add(ToItem(child));

            _treeView.SetRootItems(roots);
            _treeView.Rebuild();
            _treeView.ExpandAll();

            RefreshCount();
        }

        TreeViewItemData<int> ToItem(UpkgExportTree.Node node)
        {
            int id = _flat.Count;
            _flat.Add(node);
            _byId[id] = node;

            if (node.IsLeaf || node.Children == null || node.Children.Count == 0)
                return new TreeViewItemData<int>(id, id);

            var children = new List<TreeViewItemData<int>>(node.Children.Count);
            foreach (var child in node.Children) children.Add(ToItem(child));
            return new TreeViewItemData<int>(id, id, children);
        }

        static VisualElement MakeTreeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("tree-row");

            var toggle = new Toggle { name = "pick" };
            row.Add(toggle);

            var name = new Label { name = "name" };
            name.AddToClassList("tree-name");
            row.Add(name);

            var counts = new Label { name = "counts" };
            counts.AddToClassList("pill");
            row.Add(counts);

            return row;
        }

        void BindTreeRow(VisualElement element, int index)
        {
            var id = _treeView.GetItemDataForIndex<int>(index);
            UpkgExportTree.Node node;
            if (!_byId.TryGetValue(id, out node)) return;

            var toggle = element.Q<Toggle>("pick");
            var name = element.Q<Label>("name");
            var counts = element.Q<Label>("counts");

            element.EnableInClassList("tree-row--odd", (index & 1) == 1);

            toggle.SetValueWithoutNotify(node.Selected > 0);
            toggle.showMixedValue = node.Mixed;

            toggle.UnregisterCallback<ChangeEvent<bool>>(OnRowToggled);
            toggle.userData = node;
            toggle.RegisterCallback<ChangeEvent<bool>>(OnRowToggled);

            name.text = node.Name;
            name.tooltip = node.Path;
            name.EnableInClassList("tree-name--off", node.Selected == 0);
            name.EnableInClassList("tree-name--folder", !node.IsLeaf);

            counts.text = node.IsLeaf ? "" : $"{node.Selected}/{node.Total}";
            counts.EnableInClassList("hidden", string.IsNullOrEmpty(counts.text));
        }

        void OnRowToggled(ChangeEvent<bool> e)
        {
            var toggle = e.target as Toggle;
            if (toggle == null) return;
            var node = toggle.userData as UpkgExportTree.Node;
            if (node == null) return;

            UpkgExportTree.SetAll(node, e.newValue);
            // While "Include Dependencies" is checked, checking anything in the
            // tree cascades to its dependencies too - same as Unity's own dialog.
            if (e.newValue && _includeDepsToggle != null && _includeDepsToggle.value)
                IncludeDependencies();

            UpkgExportTree.Recount(_tree);
            _treeView.RefreshItems();
            RefreshCount();
        }

        void SetAllAndRefresh(bool value)
        {
            UpkgExportTree.SetAll(_tree, value);
            UpkgExportTree.Recount(_tree);
            _treeView.RefreshItems();
            RefreshCount();
        }

        /// <summary>Re-checks every node that's a dependency of something already
        /// checked. Called when "Include Dependencies" is turned on, and again on
        /// every row check made while it's on - matches Unity's own checkbox.</summary>
        void IncludeDependencies()
        {
            var checkedPaths = UpkgExportTree.CheckedPaths(_tree).ToArray();
            var deps = new HashSet<string>(
                AssetDatabase.GetDependencies(checkedPaths, true), StringComparer.OrdinalIgnoreCase);

            foreach (var node in _byId.Values)
                if (node.IsLeaf && deps.Contains(node.Path))
                    node.Checked = true;

            UpkgExportTree.Recount(_tree);
            _treeView.RefreshItems();
            RefreshCount();
        }

        void RefreshCount()
        {
            _countPill.text = $"{_tree.Selected} of {_tree.Total}";
        }

        // ---- export ----------------------------------------------------

        void DoExport()
        {
            var paths = UpkgExportTree.CheckedPaths(_tree).ToArray();
            if (paths.Length == 0) return;

            var output = EditorUtility.SaveFilePanel("Export protected package", "", _displayName, "unitypackage");
            if (string.IsNullOrEmpty(output)) return;

            var method = UpkgCrypto.Methods[0];   // password is the only one so far
            var credential = method.PromptForNewCredential(Path.GetFileName(output));
            if (credential == null) return;

            var rawTemp = Path.Combine(Path.GetTempPath(), $"importguard_export_{Guid.NewGuid():N}");
            try
            {
                WriteRawTar(paths, rawTemp, (f, msg) =>
                    EditorUtility.DisplayProgressBar("Import Guard", msg, f));

                UpkgTwoInOne.Write(rawTemp, output, method, credential, _displayName,
                    (phase, frac) => EditorUtility.DisplayProgressBar("Import Guard", phase, frac));

                Debug.Log($"[Import Guard] exported {paths.Length} asset(s), protected, to {output}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                try { File.Delete(rawTemp); } catch { /* best-effort */ }
            }

            Close();
        }

        /// <summary>Writes a raw (uncompressed) tar containing exactly these asset
        /// paths, read straight off disk - see UpkgArchive.Writer.Raw for why no
        /// gzip layer here.</summary>
        static void WriteRawTar(string[] assetPaths, string outputPath, Action<float, string> onProgress)
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            using (var writer = UpkgArchive.Writer.Raw(outputPath))
            {
                for (int i = 0; i < assetPaths.Length; i++)
                {
                    var path = assetPaths[i];
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid)) continue;

                    writer.Add(guid, "pathname", Encoding.UTF8.GetBytes(path));

                    var full = Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
                    if (!AssetDatabase.IsValidFolder(path) && File.Exists(full))
                        writer.Add(guid, "asset", File.ReadAllBytes(full));

                    var metaPath = $"{full}.meta";
                    if (File.Exists(metaPath))
                        writer.Add(guid, "asset.meta", File.ReadAllBytes(metaPath));

                    if (onProgress != null)
                        onProgress((float)(i + 1) / assetPaths.Length, "Writing package...");
                }
            }
        }
    }
}
