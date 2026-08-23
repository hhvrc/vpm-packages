using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;

public class AssetDuplicateFinderAndMerger : EditorWindow
{
    private Vector2 scroll;
    private bool scanCompleted;

    private enum AssetKind
    {
        Texture,
        Audio,
        Animation
    }

    private class AssetInfo
    {
        public string guid;
        public string path;
        public long byteSize;
        public string extension;
        public string hash;
        public AssetKind kind;
        public int dependencyCount;
        public int maxTextureSize;
        public bool crunchCompression;
        public int compressionQuality;
    }

    // ===================== CACHE =====================

    private const string HashCachePath = "Library/AssetDuplicateFinderHashCache.json";
    private const string DepCachePath = "Library/AssetDuplicateFinderDepCache.json";

    [Serializable]
    private class HashCacheEntry
    {
        public string guid;
        public string path;
        public long size;
        public long lastWriteTicks;
        public string hash;
    }

    [Serializable]
    private class HashCacheData
    {
        public List<HashCacheEntry> entries = new();
    }

    [Serializable]
    private class DepCacheEntry
    {
        public string path;
        public long lastWriteTicks;
        public string[] dependencies;
    }

    [Serializable]
    private class DepCacheData
    {
        public List<DepCacheEntry> entries = new();
    }

    // guid -> cached entry
    private Dictionary<string, HashCacheEntry> hashCache = new();
    // path -> cached entry
    private Dictionary<string, DepCacheEntry> depCache = new();

    private int cacheHits;
    private int cacheMisses;
    private int depCacheHits;
    private int depCacheMisses;
    private string lastScanStats;

    // ===================== DATA =====================

    private readonly Dictionary<string, List<AssetInfo>> duplicateGroups = new();
    private readonly Dictionary<string, string> championSelection = new();

    private Dictionary<string, List<string>> reverseDependencyCache;
    private Dictionary<string, int> reverseDependencyCount;

    // priority folder list editable from UI
    private List<string> priorityFolders = new()
    {
        "Assets/_PoiyomiShaders",
        "Packages/",
        "Assets/Av3Creator",
        "Assets/!Wholesome",
        "Assets/!Dismay Custom",
        "Assets/Hai"
    };

    [MenuItem("Tools/HeavenVR/Asset Duplicate Finder && Merger")]
    public static void Open()
    {
        GetWindow<AssetDuplicateFinderAndMerger>("Asset Duplicate Finder");
    }

    private void OnEnable()
    {
        LoadHashCache();
        LoadDepCache();
    }

    // ===================== UI =====================

    private void OnGUI()
    {
        GUILayout.Label("Asset Duplicate Finder & Merger", EditorStyles.boldLabel);

        // Priority Folders Section
        EditorGUILayout.LabelField("Priority Folders (Champion Preference)", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        int removeIndex = -1;
        for (int i = 0; i < priorityFolders.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            priorityFolders[i] = EditorGUILayout.TextField(priorityFolders[i]);

            if (GUILayout.Button("X", GUILayout.Width(25)))
            {
                removeIndex = i;
            }

            EditorGUILayout.EndHorizontal();
        }
        if (removeIndex >= 0)
        {
            priorityFolders.RemoveAt(removeIndex);
        }

        if (GUILayout.Button("Add Folder"))
        {
            priorityFolders.Add("Assets/");
        }

        // Update groups if folders changed
        if (EditorGUI.EndChangeCheck() && scanCompleted)
        {
            UpdateChampionSelectionForAllGroups();
        }

        GUILayout.Space(10);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Scan (Incremental)", GUILayout.Height(28)))
            Scan(false);
        if (GUILayout.Button("Full Rescan", GUILayout.Height(28)))
            Scan(true);
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(lastScanStats))
            EditorGUILayout.HelpBox(lastScanStats, MessageType.Info);

        if (!scanCompleted)
        {
            EditorGUILayout.HelpBox(
                "Scans Textures, AudioClips, and AnimationClips for byte-identical duplicates.\n" +
                "Incremental scan reuses cached hashes for unchanged files (same size & date).",
                MessageType.Info);
            return;
        }

        if (duplicateGroups.Count == 0)
        {
            EditorGUILayout.HelpBox("No duplicate assets remaining.", MessageType.Info);
            return;
        }

        if (GUILayout.Button("MERGE ALL GROUPS", GUILayout.Height(30)))
        {
            if (EditorUtility.DisplayDialog(
                "Merge All",
                "This will rewrite references and delete duplicate assets.\n\nVersion control strongly recommended.",
                "Merge",
                "Cancel"))
            {
                MergeGroups(duplicateGroups.Keys.ToList());
            }
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);

        // iterate over snapshot to allow removal during merge
        foreach (var kv in GetSortedDuplicateGroups().ToList())
            DrawGroup(kv.Key, kv.Value);

        EditorGUILayout.EndScrollView();
    }

    private void DrawGroup(string hash, List<AssetInfo> assets)
    {
        if (assets.Count < 2 || !duplicateGroups.ContainsKey(hash))
            return;

        EnsureChampionSelected(hash, assets);
        var championGuid = championSelection.ContainsKey(hash) ? championSelection[hash] : null;
        var championAsset = championGuid != null ? assets.FirstOrDefault(a => a.guid == championGuid) : null;

        // Set background color if ambiguous
        var prevColor = GUI.backgroundColor;
        if (championGuid == null)
            GUI.backgroundColor = Color.Lerp(Color.white, new Color(1f, 0.65f, 0f), 0.5f); // light orange

        EditorGUILayout.BeginVertical("box");

        GUILayout.Label($"Duplicate Group ({assets.Count})", EditorStyles.boldLabel);

        foreach (var asset in assets)
        {
            EditorGUILayout.BeginHorizontal();

            bool isChampion = asset.guid == championGuid;
            bool toggle = GUILayout.Toggle(isChampion, "Champion", GUILayout.Width(80));
            if (toggle && !isChampion)
                championSelection[hash] = asset.guid;

            if (GUILayout.Button("Ping", GUILayout.Width(40)))
                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(asset.path));

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(asset.path);
            EditorGUILayout.LabelField($"Referenced By: {asset.dependencyCount}");
            if (isChampion)
                EditorGUILayout.LabelField("ROLE: CHAMPION");

            // --- Texture differences ---
            if (asset.kind == AssetKind.Texture)
            {
                EditorGUILayout.LabelField(
                    $"Max Size: {asset.maxTextureSize}, Crunch: {asset.crunchCompression}, Quality: {asset.compressionQuality}");

                // Highlight differences from champion
                if (championAsset != null && !isChampion)
                {
                    bool diff = asset.maxTextureSize != championAsset.maxTextureSize ||
                                asset.crunchCompression != championAsset.crunchCompression ||
                                asset.compressionQuality != championAsset.compressionQuality;

                    if (diff)
                        EditorGUILayout.HelpBox("Texture settings differ from champion!", MessageType.Warning);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        if (GUILayout.Button("MERGE THIS GROUP", GUILayout.Height(25)))
        {
            if (championGuid == null)
            {
                EditorUtility.DisplayDialog(
                    "Ambiguous Champion",
                    "Multiple assets in priority folders exist. Please manually select a champion.",
                    "OK");
            }
            else if (EditorUtility.DisplayDialog(
                "Merge Group",
                $"Merge using:\n\n{AssetDatabase.GUIDToAssetPath(championGuid)}",
                "Merge",
                "Cancel"))
            {
                MergeGroups(new[] { hash });
            }
        }

        EditorGUILayout.EndVertical();
        GUI.backgroundColor = prevColor; // restore
        GUILayout.Space(8);
    }

    // ===================== CACHE I/O =====================

    private void LoadHashCache()
    {
        hashCache.Clear();
        if (!File.Exists(HashCachePath)) return;

        try
        {
            var json = File.ReadAllText(HashCachePath);
            var data = JsonUtility.FromJson<HashCacheData>(json);
            if (data?.entries != null)
            {
                foreach (var e in data.entries)
                    hashCache[e.guid] = e;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AssetDuplicateFinder] Failed to load hash cache: {ex.Message}");
        }
    }

    private void SaveHashCache()
    {
        var data = new HashCacheData { entries = hashCache.Values.ToList() };
        try
        {
            File.WriteAllText(HashCachePath, JsonUtility.ToJson(data, false));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AssetDuplicateFinder] Failed to save hash cache: {ex.Message}");
        }
    }

    private void LoadDepCache()
    {
        depCache.Clear();
        if (!File.Exists(DepCachePath)) return;

        try
        {
            var json = File.ReadAllText(DepCachePath);
            var data = JsonUtility.FromJson<DepCacheData>(json);
            if (data?.entries != null)
            {
                foreach (var e in data.entries)
                    depCache[e.path] = e;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AssetDuplicateFinder] Failed to load dep cache: {ex.Message}");
        }
    }

    private void SaveDepCache()
    {
        var data = new DepCacheData { entries = depCache.Values.ToList() };
        try
        {
            File.WriteAllText(DepCachePath, JsonUtility.ToJson(data, false));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AssetDuplicateFinder] Failed to save dep cache: {ex.Message}");
        }
    }

    // ===================== SCAN =====================

    private void Scan(bool fullRescan)
    {
        scanCompleted = false;
        duplicateGroups.Clear();
        championSelection.Clear();
        cacheHits = 0;
        cacheMisses = 0;
        depCacheHits = 0;
        depCacheMisses = 0;

        if (fullRescan)
        {
            hashCache.Clear();
            depCache.Clear();
        }

        reverseDependencyCache = new();
        reverseDependencyCount = new();

        try
        {
            BuildDependencyGraph();

            ScanType("t:Texture", AssetKind.Texture);
            ScanType("t:AudioClip", AssetKind.Audio);
            ScanType("t:AnimationClip", AssetKind.Animation);

            // Prune cache entries for files that no longer exist
            PruneHashCache();

            SaveHashCache();
            SaveDepCache();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        int totalHashed = cacheHits + cacheMisses;
        int totalDeps = depCacheHits + depCacheMisses;
        int dupeGroupCount = duplicateGroups.Count(g => g.Value.Count > 1);
        lastScanStats = $"Hashes: {cacheHits}/{totalHashed} cached, {cacheMisses} computed. " +
                        $"Dependencies: {depCacheHits}/{totalDeps} cached. " +
                        $"Found {dupeGroupCount} duplicate group(s).";

        scanCompleted = true;
        Repaint();
    }

    private void PruneHashCache()
    {
        var stale = hashCache.Keys
            .Where(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                return string.IsNullOrEmpty(path) || !File.Exists(path);
            })
            .ToList();

        foreach (var guid in stale)
            hashCache.Remove(guid);
    }

    private void ScanType(string filter, AssetKind kind)
    {
        var guids = AssetDatabase.FindAssets(filter);
        var buckets = new Dictionary<(long, string), List<AssetInfo>>();

        for (int i = 0; i < guids.Length; i++)
        {
            EditorUtility.DisplayProgressBar(
                "Scanning Assets",
                $"Indexing {kind}: {i + 1}/{guids.Length}",
                (float)i / guids.Length);

            var guid = guids[i];
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!File.Exists(path))
                continue;

            var fi = new FileInfo(path);
            var info = new AssetInfo
            {
                guid = guid,
                path = path,
                byteSize = fi.Length,
                extension = fi.Extension.ToLowerInvariant(),
                kind = kind,
                dependencyCount = reverseDependencyCount.TryGetValue(path, out var c) ? c : 0
            };

            if (kind == AssetKind.Texture)
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer is not null)
                {
                    info.maxTextureSize = importer.maxTextureSize;
                    info.crunchCompression = importer.textureCompression == TextureImporterCompression.Compressed;
                    info.compressionQuality = importer.compressionQuality;
                }
            }

            var key = (info.byteSize, info.extension);
            buckets.TryAdd(key, new List<AssetInfo>());
            buckets[key].Add(info);
        }

        foreach (var bucket in buckets.Values.Where(b => b.Count > 1))
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                var asset = bucket[i];
                EditorUtility.DisplayProgressBar(
                    $"Hashing {kind}",
                    $"{asset.path} ({i + 1}/{bucket.Count})",
                    (float)i / bucket.Count);

                asset.hash = GetOrComputeHash(asset.guid, asset.path, asset.byteSize);
                if (asset.hash == null)
                    continue;

                duplicateGroups.TryAdd(asset.hash, new List<AssetInfo>());
                duplicateGroups[asset.hash].Add(asset);
            }
        }
    }

    private string GetOrComputeHash(string guid, string path, long currentSize)
    {
        var fi = new FileInfo(path);
        long currentTicks = fi.LastWriteTimeUtc.Ticks;

        if (hashCache.TryGetValue(guid, out var cached))
        {
            if (cached.size == currentSize && cached.lastWriteTicks == currentTicks && cached.hash != null)
            {
                // Update path in case asset was moved
                cached.path = path;
                cacheHits++;
                return cached.hash;
            }
        }

        cacheMisses++;
        var hash = ComputeHash(path);
        if (hash != null)
        {
            hashCache[guid] = new HashCacheEntry
            {
                guid = guid,
                path = path,
                size = currentSize,
                lastWriteTicks = currentTicks,
                hash = hash
            };
        }
        return hash;
    }

    // ===================== MERGE =====================

    private void MergeGroups(IEnumerable<string> hashes)
    {
        var oldSerialization = EditorSettings.serializationMode;
        EditorSettings.serializationMode = SerializationMode.ForceText;

        try
        {
            AssetDatabase.StartAssetEditing();
            var hashList = hashes.ToList();

            for (int h = 0; h < hashList.Count; h++)
            {
                EditorUtility.DisplayProgressBar(
                    "Merging Duplicates",
                    $"Processing group {h + 1}/{hashList.Count}",
                    (float)h / hashList.Count);

                var hash = hashList[h];
                if (!duplicateGroups.TryGetValue(hash, out var assets)) continue;

                var championGuid = championSelection.ContainsKey(hash) ? championSelection[hash] : null;
                if (championGuid == null) continue; // skip ambiguous groups

                foreach (var asset in assets)
                {
                    if (asset.guid == championGuid)
                        continue;

                    var dependents = reverseDependencyCache.TryGetValue(asset.path, out var list)
                        ? list
                        : new List<string>();

                    RewriteReferences(asset.guid, championGuid, dependents);

                    // Remove from hash cache since it's being deleted
                    hashCache.Remove(asset.guid);

                    AssetDatabase.DeleteAsset(asset.path);
                }

                duplicateGroups.Remove(hash);
                championSelection.Remove(hash);
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();
            EditorSettings.serializationMode = oldSerialization;
            SaveHashCache();
            Repaint();
        }
    }

    private void RewriteReferences(string fromGuid, string toGuid, List<string> files)
    {
        for (int i = 0; i < files.Count; i++)
        {
            var path = files[i];
            if (!IsTextAsset(path)) continue;

            EditorUtility.DisplayProgressBar(
                "Updating References",
                $"Rewriting: {path} ({i + 1}/{files.Count})",
                (float)i / files.Count);

            var text = File.ReadAllText(path);
            if (!text.Contains(fromGuid))
                continue;

            Undo.RegisterCompleteObjectUndo(
                AssetDatabase.LoadMainAssetAtPath(path),
                "Merge Duplicate Assets");

            File.WriteAllText(path, text.Replace(fromGuid, toGuid));
        }
    }

    // ===================== DEPENDENCY GRAPH =====================

    private void BuildDependencyGraph()
    {
        reverseDependencyCache = new();
        reverseDependencyCount = new();

        var allAssets = AssetDatabase.GetAllAssetPaths()
            .Where(p => p.StartsWith("Assets/") && !p.EndsWith(".meta"))
            .ToArray();

        for (int i = 0; i < allAssets.Length; i++)
        {
            EditorUtility.DisplayProgressBar(
                "Building Dependency Graph",
                $"Processing {i + 1}/{allAssets.Length}",
                (float)i / allAssets.Length);

            var asset = allAssets[i];
            var deps = GetDependenciesCachedOrCompute(asset);
            foreach (var dep in deps)
            {
                if (dep == asset)
                    continue;

                reverseDependencyCache.TryAdd(dep, new List<string>());
                reverseDependencyCache[dep].Add(asset);

                reverseDependencyCount[dep] =
                    reverseDependencyCount.TryGetValue(dep, out var c) ? c + 1 : 1;
            }
        }
    }

    private string[] GetDependenciesCachedOrCompute(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<string>();

        long currentTicks = new FileInfo(path).LastWriteTimeUtc.Ticks;

        if (depCache.TryGetValue(path, out var cached))
        {
            if (cached.lastWriteTicks == currentTicks && cached.dependencies != null)
            {
                depCacheHits++;
                return cached.dependencies;
            }
        }

        depCacheMisses++;
        var deps = AssetDatabase.GetDependencies(path, false);

        depCache[path] = new DepCacheEntry
        {
            path = path,
            lastWriteTicks = currentTicks,
            dependencies = deps
        };

        return deps;
    }

    // ===================== HELPERS =====================

    private void EnsureChampionSelected(string hash, List<AssetInfo> assets)
    {
        if (championSelection.ContainsKey(hash))
            return;

        var priorityAssets = assets
            .Where(a => priorityFolders.Any(folder => a.path.StartsWith(folder)))
            .ToList();

        if (priorityAssets.Count == 1)
        {
            championSelection[hash] = priorityAssets[0].guid;
        }
        else if (priorityAssets.Count > 1)
        {
            championSelection[hash] = null; // ambiguous
        }
        else
        {
            championSelection[hash] = assets
                .OrderByDescending(a => a.dependencyCount)
                .ThenBy(a => a.guid)
                .First().guid;
        }
    }

    private void UpdateChampionSelectionForAllGroups()
    {
        foreach (var kv in duplicateGroups)
            championSelection.Remove(kv.Key);

        foreach (var kv in duplicateGroups)
            EnsureChampionSelected(kv.Key, kv.Value);
    }

    private IEnumerable<KeyValuePair<string, List<AssetInfo>>> GetSortedDuplicateGroups()
    {
        return duplicateGroups
            .Where(g => g.Value.Count > 1)
            .OrderByDescending(g => g.Value.Any(a => priorityFolders.Any(f => a.path.StartsWith(f)))) // priority first
            .ThenByDescending(g => g.Value.Count)
            .ThenByDescending(g => g.Value.Sum(a => a.dependencyCount));
    }

    private static bool IsTextAsset(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is
            ".prefab" or ".unity" or ".asset" or ".controller" or
            ".mat" or ".anim" or ".overridecontroller" or
            ".playable" or ".spriteatlas" or ".lighting" or
            ".vfx" or ".shadergraph" or ".compute" or ".fbx";
    }

    private static string ComputeHash(string assetPath)
    {
        try
        {
            using var stream = File.OpenRead(assetPath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);

            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }
}
