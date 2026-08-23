using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MaterialConsolidator : EditorWindow
{
    [MenuItem("Tools/HeavenVR/Material Consolidator")]
    static void ShowWindow() => GetWindow<MaterialConsolidator>("Material Consolidator");

    enum SourceKind { Scene, Prefab }

    struct RendererEntry
    {
        public SourceKind kind;
        public string sourcePath;
        public string hierarchyPath;
        public Renderer renderer;
        public Mesh mesh;
        public Material[] materials;
    }

    class MaterialNameAnalysis
    {
        public string fbxMaterialName;
        public Material currentDefault;
        public Material consensus;
        public bool canConsolidate;
        public bool hasConflict;
        public int nullMaterialCount;
        public int totalAssignments;
        public Dictionary<Material, int> materialCounts = new Dictionary<Material, int>();
    }

    class ModelAnalysis
    {
        public string modelPath;
        public int totalInstances;
        public List<MaterialNameAnalysis> materials = new List<MaterialNameAnalysis>();
        public HashSet<string> scenePaths = new HashSet<string>();
        public HashSet<string> prefabPaths = new HashSet<string>();
        public bool foldout;
        public bool selected;
        public bool hasActionable => materials.Any(m => m.canConsolidate);
    }

    class RedundantOverrideEntry
    {
        public string sourcePath;
        public string instancePath;
        public string rendererPath;
        public int slotIndex;
        public Material material;
    }

    class ExcessSlotsEntry
    {
        public SourceKind kind;
        public string sourcePath;
        public string hierarchyPath;
        public Renderer sceneRenderer;
        public int subMeshCount;
        public int slotCount;
    }

    int _tab;
    Vector2 _scroll;
    bool _includePrefabs = true;

    // Pass 1
    List<ModelAnalysis> _models = new List<ModelAnalysis>();
    // Pass 2
    List<RedundantOverrideEntry> _sceneOverrides = new List<RedundantOverrideEntry>();
    // Pass 3
    List<RedundantOverrideEntry> _prefabOverrides = new List<RedundantOverrideEntry>();

    bool _consolidateScanned;
    string _consolidateStatus;

    bool _pass2Foldout = true;
    bool _pass3Foldout = true;

    List<ExcessSlotsEntry> _excessEntries = new List<ExcessSlotsEntry>();
    bool _cleanupScanned;
    string _cleanupStatus;

    // ─── UI ───

    void OnGUI()
    {
        _tab = GUILayout.Toolbar(_tab, new[] { "Consolidate", "Cleanup" });
        EditorGUILayout.Space();

        _includePrefabs = EditorGUILayout.Toggle("Include Prefab Assets", _includePrefabs);
        EditorGUILayout.Space();

        if (_tab == 0) DrawConsolidateTab();
        else DrawCleanupTab();
    }

    void DrawConsolidateTab()
    {
        EditorGUILayout.HelpBox(
            "Pass 1: Consolidate unanimous material assignments into model import settings.\n" +
            "Pass 2: Remove redundant material overrides on prefab instances in scenes.\n" +
            "Pass 3: Remove redundant material overrides inside nested prefabs.",
            MessageType.Info);

        if (GUILayout.Button("Scan", GUILayout.Height(28)))
            RunConsolidateScan();

        if (!string.IsNullOrEmpty(_consolidateStatus))
            EditorGUILayout.HelpBox(_consolidateStatus, MessageType.Info);

        if (!_consolidateScanned) return;

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        DrawPass1Section();
        DrawPass2Section();
        DrawPass3Section();

        EditorGUILayout.EndScrollView();

        int pass1Count = _models.Count(m => m.selected && m.hasActionable);
        int totalActions = pass1Count + _sceneOverrides.Count + _prefabOverrides.Count;

        EditorGUI.BeginDisabledGroup(totalActions == 0);
        if (GUILayout.Button($"Apply ({pass1Count} model(s), {_sceneOverrides.Count} scene override(s), {_prefabOverrides.Count} prefab override(s))", GUILayout.Height(28)))
        {
            if (EditorUtility.DisplayDialog("Consolidate Materials",
                $"Pass 1: Update import settings for {pass1Count} model(s)\n" +
                $"Pass 2: Remove {_sceneOverrides.Count} redundant scene override(s)\n" +
                $"Pass 3: Remove {_prefabOverrides.Count} redundant prefab override(s)\n\nProceed?",
                "Apply", "Cancel"))
                ApplyAll();
        }
        EditorGUI.EndDisabledGroup();
    }

    void DrawPass1Section()
    {
        EditorGUILayout.LabelField("Pass 1: Model Import Consolidation", EditorStyles.boldLabel);

        var visible = _models.Where(m => m.hasActionable).ToList();
        if (visible.Count == 0)
        {
            EditorGUILayout.LabelField("  No materials to consolidate.");
            EditorGUILayout.Space();
            return;
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Select All", EditorStyles.miniButtonLeft))
            foreach (var m in _models) m.selected = m.hasActionable;
        if (GUILayout.Button("Select None", EditorStyles.miniButtonRight))
            foreach (var m in _models) m.selected = false;
        EditorGUILayout.EndHorizontal();

        foreach (var m in visible)
            DrawModelAnalysis(m);

        EditorGUILayout.Space();
    }

    void DrawPass2Section()
    {
        EditorGUILayout.LabelField("Pass 2: Redundant Scene Overrides", EditorStyles.boldLabel);

        if (_sceneOverrides.Count == 0)
        {
            EditorGUILayout.LabelField("  No redundant scene overrides found.");
            EditorGUILayout.Space();
            return;
        }

        _pass2Foldout = EditorGUILayout.Foldout(_pass2Foldout,
            $"{_sceneOverrides.Count} redundant override(s) in scenes", true);

        if (_pass2Foldout)
        {
            EditorGUI.indentLevel++;
            var byScene = _sceneOverrides.GroupBy(o => o.sourcePath);
            foreach (var group in byScene)
            {
                EditorGUILayout.LabelField(Path.GetFileName(group.Key), EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                foreach (var o in group)
                    EditorGUILayout.LabelField($"{o.rendererPath} slot[{o.slotIndex}]: {(o.material ? o.material.name : "null")}");
                EditorGUI.indentLevel--;
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space();
    }

    void DrawPass3Section()
    {
        EditorGUILayout.LabelField("Pass 3: Redundant Prefab Overrides", EditorStyles.boldLabel);

        if (_prefabOverrides.Count == 0)
        {
            EditorGUILayout.LabelField("  No redundant prefab overrides found.");
            EditorGUILayout.Space();
            return;
        }

        _pass3Foldout = EditorGUILayout.Foldout(_pass3Foldout,
            $"{_prefabOverrides.Count} redundant override(s) in prefabs", true);

        if (_pass3Foldout)
        {
            EditorGUI.indentLevel++;
            var byPrefab = _prefabOverrides.GroupBy(o => o.sourcePath);
            foreach (var group in byPrefab)
            {
                EditorGUILayout.LabelField(Path.GetFileName(group.Key), EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                foreach (var o in group)
                    EditorGUILayout.LabelField($"{o.rendererPath} slot[{o.slotIndex}]: {(o.material ? o.material.name : "null")}");
                EditorGUI.indentLevel--;
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space();
    }

    void DrawModelAnalysis(ModelAnalysis model)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        model.foldout = EditorGUILayout.Foldout(model.foldout,
            $"{Path.GetFileName(model.modelPath)}  ({model.totalInstances} instance{(model.totalInstances != 1 ? "s" : "")})",
            true);
        model.selected = EditorGUILayout.Toggle(model.selected, GUILayout.Width(20));
        EditorGUILayout.EndHorizontal();

        if (model.foldout)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Path", model.modelPath);

            if (model.scenePaths.Count > 0)
                EditorGUILayout.LabelField("Scenes", string.Join(", ", model.scenePaths.Select(Path.GetFileName)));
            if (model.prefabPaths.Count > 0)
            {
                var prefabNames = model.prefabPaths.Select(Path.GetFileName).Take(5);
                var label = string.Join(", ", prefabNames);
                if (model.prefabPaths.Count > 5)
                    label += $" (+{model.prefabPaths.Count - 5} more)";
                EditorGUILayout.LabelField("Prefabs", label);
            }

            foreach (var mat in model.materials)
            {
                if (mat.canConsolidate)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(
                        $"\"{mat.fbxMaterialName}\":",
                        GUILayout.Width(180));
                    EditorGUILayout.ObjectField(mat.consensus, typeof(Material), false);
                    EditorGUILayout.LabelField(
                        $"(default: {(mat.currentDefault ? mat.currentDefault.name : "none")})",
                        GUILayout.Width(200));
                    EditorGUILayout.EndHorizontal();
                }
                else if (mat.hasConflict)
                {
                    int distinctCount = mat.materialCounts.Count + (mat.nullMaterialCount > 0 ? 1 : 0);
                    EditorGUILayout.LabelField(
                        $"\"{mat.fbxMaterialName}\": conflict ({distinctCount} different across {mat.totalAssignments} refs)");

                    EditorGUI.indentLevel++;
                    foreach (var kv in mat.materialCounts.OrderByDescending(x => x.Value))
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.ObjectField(kv.Key, typeof(Material), false, GUILayout.Width(200));
                        EditorGUILayout.LabelField($"{kv.Value} ref{(kv.Value != 1 ? "s" : "")}");
                        EditorGUILayout.EndHorizontal();
                    }
                    if (mat.nullMaterialCount > 0)
                        EditorGUILayout.LabelField($"(null): {mat.nullMaterialCount} ref{(mat.nullMaterialCount != 1 ? "s" : "")}");
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndVertical();
    }

    void DrawCleanupTab()
    {
        EditorGUILayout.HelpBox(
            "Finds renderers with more material slots than their mesh has submeshes " +
            "and trims the unused excess slots.",
            MessageType.Info);

        if (GUILayout.Button("Scan", GUILayout.Height(28)))
            RunCleanupScan();

        if (!string.IsNullOrEmpty(_cleanupStatus))
            EditorGUILayout.HelpBox(_cleanupStatus, MessageType.Info);

        if (!_cleanupScanned || _excessEntries.Count == 0) return;

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (var e in _excessEntries)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(e.hierarchyPath, EditorStyles.boldLabel);
            string source = e.kind == SourceKind.Scene ? "Scene" : "Prefab";
            EditorGUILayout.LabelField($"{source}: {e.sourcePath}");
            EditorGUILayout.LabelField(
                $"Submeshes: {e.subMeshCount}   Material slots: {e.slotCount}   (+{e.slotCount - e.subMeshCount} excess)");
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();

        if (GUILayout.Button($"Fix All ({_excessEntries.Count})", GUILayout.Height(28)))
        {
            if (EditorUtility.DisplayDialog("Cleanup Material Slots",
                $"Trim excess material slots on {_excessEntries.Count} renderer(s)?",
                "Fix", "Cancel"))
                ApplyCleanup();
        }
    }

    // ─── Helpers ───

    static Mesh GetMesh(Renderer r)
    {
        if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
        var mf = r.GetComponent<MeshFilter>();
        return mf ? mf.sharedMesh : null;
    }

    static string HierarchyPath(Transform t)
    {
        var parts = new List<string>();
        while (t != null) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }

    static int ParseMaterialSlotIndex(string propertyPath)
    {
        int start = propertyPath.IndexOf('[');
        int end = propertyPath.IndexOf(']');
        if (start < 0 || end < 0 || end <= start + 1) return -1;
        if (int.TryParse(propertyPath.Substring(start + 1, end - start - 1), out int index))
            return index;
        return -1;
    }

    /// <summary>
    /// Checks if a PropertyModification is a redundant material override.
    /// mod.target is the source Renderer (on the source prefab asset).
    /// mod.objectReference is the overriding Material.
    /// Redundant = the override sets the same material the source already has.
    /// </summary>
    static bool IsRedundantMaterialOverride(PropertyModification mod)
    {
        if (mod.objectReference == null) return false;
        if (!mod.propertyPath.StartsWith("m_Materials.Array.data[")) return false;
        if (mod.target is not Renderer sourceRenderer) return false;

        int slotIndex = ParseMaterialSlotIndex(mod.propertyPath);
        if (slotIndex < 0) return false;

        var sourceMats = sourceRenderer.sharedMaterials;
        if (slotIndex >= sourceMats.Length) return false;

        return (mod.objectReference as Material) == sourceMats[slotIndex];
    }

    // ─── Collecting renderers ───

    List<RendererEntry> CollectRenderers()
    {
        var list = new List<RendererEntry>();

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = GetMesh(r);
                if (mesh == null) continue;
                list.Add(new RendererEntry
                {
                    kind = SourceKind.Scene,
                    sourcePath = scene.path,
                    hierarchyPath = HierarchyPath(r.transform),
                    renderer = r,
                    mesh = mesh,
                    materials = r.sharedMaterials
                });
            }
        }

        if (_includePrefabs)
        {
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            for (int gi = 0; gi < guids.Length; gi++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[gi]);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    var mesh = GetMesh(r);
                    if (mesh == null) continue;
                    list.Add(new RendererEntry
                    {
                        kind = SourceKind.Prefab,
                        sourcePath = path,
                        hierarchyPath = HierarchyPath(r.transform),
                        renderer = r,
                        mesh = mesh,
                        materials = r.sharedMaterials
                    });
                }
            }
        }

        return list;
    }

    // ─── Pass 1: Consolidation scan ───

    void ScanPass1(List<RendererEntry> entries)
    {
        _models.Clear();

        var byModel = new Dictionary<string, List<RendererEntry>>();
        foreach (var e in entries)
        {
            var modelPath = AssetDatabase.GetAssetPath(e.mesh);
            if (string.IsNullOrEmpty(modelPath)) continue;
            if (AssetImporter.GetAtPath(modelPath) is not ModelImporter) continue;

            if (!byModel.ContainsKey(modelPath))
                byModel[modelPath] = new List<RendererEntry>();
            byModel[modelPath].Add(e);
        }

        foreach (var kv in byModel)
        {
            var modelPath = kv.Key;
            var allInstances = kv.Value;
            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            var modelGO = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (modelGO == null) continue;

            var extMap = importer.GetExternalObjectMap();
            var remapTargetToName = new Dictionary<UnityEngine.Object, string>();
            var alreadyRemapped = new HashSet<string>();
            foreach (var entry in extMap)
            {
                if (entry.Key.type == typeof(Material) && entry.Value != null)
                {
                    remapTargetToName[entry.Value] = entry.Key.name;
                    alreadyRemapped.Add(entry.Key.name);
                }
            }

            var meshSlotNames = new Dictionary<Mesh, string[]>();
            var meshSlotDefaults = new Dictionary<Mesh, Material[]>();
            foreach (var r in modelGO.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = GetMesh(r);
                if (mesh == null || meshSlotNames.ContainsKey(mesh)) continue;

                var defaults = r.sharedMaterials;
                var names = new string[defaults.Length];
                for (int i = 0; i < defaults.Length; i++)
                {
                    var mat = defaults[i];
                    if (mat == null) { names[i] = $"Slot_{i}"; continue; }
                    if (AssetDatabase.GetAssetPath(mat) == modelPath)
                        names[i] = mat.name;
                    else if (remapTargetToName.TryGetValue(mat, out var n))
                        names[i] = n;
                    else
                        names[i] = mat.name;
                }
                meshSlotNames[mesh] = names;
                meshSlotDefaults[mesh] = defaults;
            }

            var matNameMap = new Dictionary<string, MaterialNameAnalysis>();
            var uniqueRenderers = new HashSet<Renderer>();
            var scenePaths = new HashSet<string>();
            var prefabPaths = new HashSet<string>();

            foreach (var inst in allInstances)
            {
                if (!meshSlotNames.TryGetValue(inst.mesh, out var slotNames)) continue;
                uniqueRenderers.Add(inst.renderer);

                if (inst.kind == SourceKind.Scene) scenePaths.Add(inst.sourcePath);
                else prefabPaths.Add(inst.sourcePath);

                int slotCount = Math.Min(inst.materials.Length, slotNames.Length);
                for (int s = 0; s < slotCount; s++)
                {
                    var fbxName = slotNames[s];
                    if (!matNameMap.TryGetValue(fbxName, out var analysis))
                    {
                        var defaults = meshSlotDefaults[inst.mesh];
                        analysis = new MaterialNameAnalysis
                        {
                            fbxMaterialName = fbxName,
                            currentDefault = s < defaults.Length ? defaults[s] : null
                        };
                        matNameMap[fbxName] = analysis;
                    }

                    analysis.totalAssignments++;
                    var mat = inst.materials[s];
                    if (mat == null)
                    {
                        analysis.nullMaterialCount++;
                    }
                    else
                    {
                        analysis.materialCounts.TryGetValue(mat, out int c);
                        analysis.materialCounts[mat] = c + 1;
                    }
                }
            }

            foreach (var analysis in matNameMap.Values)
            {
                if (analysis.materialCounts.Count == 1 && analysis.nullMaterialCount == 0)
                {
                    analysis.consensus = analysis.materialCounts.Keys.First();
                    // Only consolidate into empty remap slots — never overwrite existing remaps
                    analysis.canConsolidate = analysis.consensus != analysis.currentDefault
                        && !alreadyRemapped.Contains(analysis.fbxMaterialName);
                }
                else if (analysis.materialCounts.Count > 1 ||
                         (analysis.materialCounts.Count >= 1 && analysis.nullMaterialCount > 0))
                {
                    analysis.hasConflict = true;
                }
            }

            var model = new ModelAnalysis
            {
                modelPath = modelPath,
                totalInstances = uniqueRenderers.Count,
                scenePaths = scenePaths,
                prefabPaths = prefabPaths,
                materials = matNameMap.Values
                    .Where(m => m.canConsolidate || m.hasConflict)
                    .OrderByDescending(m => m.canConsolidate)
                    .ThenBy(m => m.fbxMaterialName)
                    .ToList()
            };
            model.selected = model.hasActionable;
            _models.Add(model);
        }

        _models.Sort((a, b) => b.hasActionable.CompareTo(a.hasActionable));
    }

    // ─── Pass 2/3: Scan for redundant material overrides ───

    /// <summary>
    /// Scans prefab instances in open scenes for redundant material overrides.
    /// An override is redundant when it sets the same material the source prefab already has.
    /// </summary>
    void ScanPass2()
    {
        _sceneOverrides.Clear();

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;

                var mods = PrefabUtility.GetPropertyModifications(t.gameObject);
                if (mods == null) continue;

                foreach (var mod in mods)
                {
                    if (!IsRedundantMaterialOverride(mod)) continue;

                    int slotIndex = ParseMaterialSlotIndex(mod.propertyPath);
                    _sceneOverrides.Add(new RedundantOverrideEntry
                    {
                        sourcePath = scene.path,
                        instancePath = HierarchyPath(t),
                        rendererPath = mod.target ? mod.target.name : "?",
                        slotIndex = slotIndex,
                        material = mod.objectReference as Material
                    });
                }
            }
        }
    }

    /// <summary>
    /// Scans nested prefab instances inside prefab assets for redundant material overrides.
    /// </summary>
    void ScanPass3()
    {
        _prefabOverrides.Clear();

        if (!_includePrefabs) return;

        var guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                {
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;

                    var mods = PrefabUtility.GetPropertyModifications(t.gameObject);
                    if (mods == null) continue;

                    foreach (var mod in mods)
                    {
                        if (!IsRedundantMaterialOverride(mod)) continue;

                        int slotIndex = ParseMaterialSlotIndex(mod.propertyPath);
                        _prefabOverrides.Add(new RedundantOverrideEntry
                        {
                            sourcePath = path,
                            instancePath = HierarchyPath(t),
                            rendererPath = mod.target ? mod.target.name : "?",
                            slotIndex = slotIndex,
                            material = mod.objectReference as Material
                        });
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
    }

    // ─── Full scan ───

    void RunConsolidateScan()
    {
        _consolidateStatus = "";
        _consolidateScanned = false;

        var entries = CollectRenderers();
        ScanPass1(entries);
        ScanPass2();
        ScanPass3();

        int actionable = _models.Count(m => m.hasActionable);
        int conflicts = _models.Sum(m => m.materials.Count(x => x.hasConflict));

        var parts = new List<string>();
        parts.Add($"Pass 1: {actionable} model(s) to consolidate");
        if (conflicts > 0)
            parts.Add($"{conflicts} conflict(s)");
        parts.Add($"Pass 2: {_sceneOverrides.Count} redundant scene override(s)");
        parts.Add($"Pass 3: {_prefabOverrides.Count} redundant prefab override(s)");

        _consolidateStatus = string.Join(". ", parts) + ".";
        _consolidateScanned = true;
        Repaint();
    }

    // ─── Apply all passes ───

    void ApplyAll()
    {
        ApplyPass1();
        // After Pass 1, model defaults changed. Pass 2/3 do live redundancy checks
        // against the now-updated source materials, so overrides that became
        // redundant due to Pass 1 consolidation are caught and removed.
        ApplyPass2();
        ApplyPass3();

        _consolidateStatus = "Applied all passes. Re-scanning...";
        RunConsolidateScan();
    }

    void ApplyPass1()
    {
        var selected = _models.Where(m => m.selected && m.hasActionable).ToList();
        int count = 0;

        foreach (var model in selected)
        {
            var importer = AssetImporter.GetAtPath(model.modelPath) as ModelImporter;
            if (importer == null) continue;

            bool modified = false;
            foreach (var mat in model.materials)
            {
                if (!mat.canConsolidate) continue;
                var sourceId = new AssetImporter.SourceAssetIdentifier(typeof(Material), mat.fbxMaterialName);
                importer.AddRemap(sourceId, mat.consensus);
                modified = true;
            }

            if (modified)
            {
                importer.SaveAndReimport();
                count++;
            }
        }

        Debug.Log($"[MaterialConsolidator] Pass 1: Updated {count} model(s).");
    }

    /// <summary>
    /// Live-checks all prefab instances in open scenes and removes any material
    /// overrides that are redundant (override value == source value).
    /// </summary>
    void ApplyPass2()
    {
        int removed = 0;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            bool sceneDirty = false;

            foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;

                var mods = PrefabUtility.GetPropertyModifications(t.gameObject);
                if (mods == null) continue;

                var filtered = mods.Where(mod => !IsRedundantMaterialOverride(mod)).ToArray();

                if (filtered.Length < mods.Length)
                {
                    PrefabUtility.SetPropertyModifications(t.gameObject, filtered);
                    removed += mods.Length - filtered.Length;
                    sceneDirty = true;
                }
            }

            if (sceneDirty)
                EditorSceneManager.MarkSceneDirty(scene);
        }

        Debug.Log($"[MaterialConsolidator] Pass 2: Removed {removed} redundant scene override(s).");
    }

    /// <summary>
    /// Iteratively removes redundant material overrides inside prefab assets.
    /// Repeats until no more are found, handling chains (A→B→C).
    /// </summary>
    void ApplyPass3()
    {
        if (!_includePrefabs) return;

        bool changed = true;
        int totalRemoved = 0;
        int iterations = 0;
        const int maxIterations = 20;

        var allPrefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });

        while (changed && iterations < maxIterations)
        {
            changed = false;
            iterations++;

            foreach (var guid in allPrefabGuids)
            {
                var prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                var contents = PrefabUtility.LoadPrefabContents(prefabPath);
                bool modified = false;

                try
                {
                    foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                    {
                        if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;

                        var mods = PrefabUtility.GetPropertyModifications(t.gameObject);
                        if (mods == null) continue;

                        var filtered = mods.Where(mod => !IsRedundantMaterialOverride(mod)).ToArray();

                        if (filtered.Length < mods.Length)
                        {
                            PrefabUtility.SetPropertyModifications(t.gameObject, filtered);
                            modified = true;
                            totalRemoved += mods.Length - filtered.Length;
                        }
                    }

                    if (modified)
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                        changed = true;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
        }

        Debug.Log($"[MaterialConsolidator] Pass 3: Removed {totalRemoved} redundant prefab override(s) in {iterations} iteration(s).");
    }

    // ─── Cleanup scan ───

    void RunCleanupScan()
    {
        _excessEntries.Clear();
        _cleanupStatus = "";

        foreach (var e in CollectRenderers())
        {
            if (e.materials.Length > e.mesh.subMeshCount)
            {
                _excessEntries.Add(new ExcessSlotsEntry
                {
                    kind = e.kind,
                    sourcePath = e.sourcePath,
                    hierarchyPath = e.hierarchyPath,
                    sceneRenderer = e.kind == SourceKind.Scene ? e.renderer : null,
                    subMeshCount = e.mesh.subMeshCount,
                    slotCount = e.materials.Length
                });
            }
        }

        _cleanupScanned = true;
        _cleanupStatus = _excessEntries.Count == 0
            ? "No renderers with excess material slots found."
            : $"Found {_excessEntries.Count} renderer(s) with excess material slots.";
        Repaint();
    }

    // ─── Cleanup apply ───

    void ApplyCleanup()
    {
        int fixedCount = 0;

        foreach (var e in _excessEntries.Where(x => x.kind == SourceKind.Scene))
        {
            var r = e.sceneRenderer;
            if (r == null) continue;
            var mesh = GetMesh(r);
            if (mesh == null) continue;

            Undo.RecordObject(r, "Trim excess material slots");
            var mats = r.sharedMaterials;
            var trimmed = new Material[mesh.subMeshCount];
            Array.Copy(mats, trimmed, Math.Min(mats.Length, trimmed.Length));
            r.sharedMaterials = trimmed;
            EditorUtility.SetDirty(r);
            fixedCount++;
        }

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded)
                EditorSceneManager.MarkSceneDirty(scene);
        }

        var prefabGroups = _excessEntries
            .Where(x => x.kind == SourceKind.Prefab)
            .GroupBy(x => x.sourcePath);

        foreach (var prefabGroup in prefabGroups)
        {
            var contents = PrefabUtility.LoadPrefabContents(prefabGroup.Key);
            bool modified = false;

            foreach (var e in prefabGroup)
            {
                foreach (var r in contents.GetComponentsInChildren<Renderer>(true))
                {
                    if (HierarchyPath(r.transform) != e.hierarchyPath) continue;
                    var mesh = GetMesh(r);
                    if (mesh == null || r.sharedMaterials.Length <= mesh.subMeshCount) continue;

                    var mats = r.sharedMaterials;
                    var trimmed = new Material[mesh.subMeshCount];
                    Array.Copy(mats, trimmed, Math.Min(mats.Length, trimmed.Length));
                    r.sharedMaterials = trimmed;
                    modified = true;
                    fixedCount++;
                }
            }

            if (modified)
                PrefabUtility.SaveAsPrefabAsset(contents, prefabGroup.Key);
            PrefabUtility.UnloadPrefabContents(contents);
        }

        _cleanupStatus = $"Trimmed excess slots on {fixedCount} renderer(s).";
        RunCleanupScan();
    }
}
