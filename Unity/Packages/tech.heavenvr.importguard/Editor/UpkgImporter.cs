using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Executes a set of decisions: writes chosen entries into the project with
    /// rewritten guids, or exports them as a new .unitypackage.
    ///
    /// Files are written directly rather than handed to Unity's importer, because
    /// that is the only way to control the guid an asset lands with.
    /// </summary>
    public static class UpkgImporter
    {
        public class Result
        {
            public int Imported;
            public int Skipped;
            public int Remapped;
            public int Redirected;
            public int ReferencesPatched;
            public List<string> Errors = new List<string>();

            public override string ToString()
            {
                return string.Format(
                    "{0} imported, {1} left out, {2} guids remapped, {3} redirected, " +
                    "{4} references rewritten",
                    Imported, Skipped, Remapped, Redirected, ReferencesPatched);
            }
        }

        /// <summary>
        /// Assigns fresh guids to everything the user chose to remap and builds the
        /// substitution map that every written payload is filtered through.
        /// </summary>
        public static Dictionary<string, string> BuildGuidMap(
            List<UpkgRow> rows, UpkgProject project)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in project.GuidToPath.Keys) taken.Add(g);
            foreach (var r in rows) taken.Add(r.Entry.Guid);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                row.NewGuid = row.Entry.Guid;

                if (row.Action == UpkgAction.Skip)
                {
                    // A skipped entry's references go to the chosen replacement, if any.
                    if (!string.IsNullOrEmpty(row.RedirectTo))
                        map[row.Entry.Guid] = row.RedirectTo;
                    continue;
                }
                if (row.Action == UpkgAction.ImportKeepGuid) continue;
                if (!row.IsConflict) continue;

                row.NewGuid = UpkgText.MintGuid(row.Entry.Guid, taken);
                map[row.Entry.Guid] = row.NewGuid;
            }
            return map;
        }

        static bool ShouldPatchPayload(UpkgEntry entry)
        {
            return UpkgText.TextAssetExtensions.Contains(entry.Extension);
        }

        static byte[] PatchMeta(byte[] meta, string newGuid, Dictionary<string, string> map,
                                out int hits)
        {
            var patched = UpkgText.Substitute(meta, map, out hits);
            if (patched == null) return null;

            // The entry's own guid may not be in the map (nothing referenced it), but
            // the .meta must still carry the guid the asset is landing with.
            var text = Encoding.GetEncoding(28591).GetString(patched);
            var replaced = System.Text.RegularExpressions.Regex.Replace(
                text, "^guid:\\s*[0-9a-fA-F]{32}", "guid: " + newGuid,
                System.Text.RegularExpressions.RegexOptions.Multiline);
            return Encoding.GetEncoding(28591).GetBytes(replaced);
        }

        /// <summary>Writes the chosen entries into the open project.</summary>
        public static Result ImportIntoProject(string packagePath, List<UpkgRow> rows,
                                               UpkgProject project,
                                               Func<float, string, bool> onProgress = null)
        {
            var result = new Result();
            var map = BuildGuidMap(rows, project);

            var byGuid = new Dictionary<string, UpkgRow>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                byGuid[r.Entry.Guid] = r;
                if (r.Action == UpkgAction.Skip)
                {
                    result.Skipped++;
                    if (!string.IsNullOrEmpty(r.RedirectTo)) result.Redirected++;
                }
                else if (r.NewGuid != r.Entry.Guid) result.Remapped++;
            }

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            bool cancelled = false;

            try
            {
                AssetDatabase.StartAssetEditing();

                UpkgArchive.Read(packagePath,
                    want: m =>
                    {
                        if (m.Name != "asset" && m.Name != "asset.meta") return false;
                        UpkgRow row;
                        return byGuid.TryGetValue(m.Guid, out row) &&
                               row.Action != UpkgAction.Skip;
                    },
                    onPayload: (m, data) =>
                    {
                        var row = byGuid[m.Guid];
                        var target = Path.Combine(projectRoot,
                            row.Entry.PathName.Replace('/', Path.DirectorySeparatorChar));

                        try
                        {
                            if (m.Name == "asset")
                            {
                                int hits = 0;
                                if (ShouldPatchPayload(row.Entry))
                                    data = UpkgText.Substitute(data, map, out hits);
                                result.ReferencesPatched += hits;

                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                                File.WriteAllBytes(target, data);
                                result.Imported++;
                            }
                            else
                            {
                                int hits;
                                var meta = PatchMeta(data, row.NewGuid, map, out hits);
                                result.ReferencesPatched += hits;

                                if (row.Entry.IsFolder)
                                {
                                    Directory.CreateDirectory(target);
                                    result.Imported++;
                                }
                                else
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                                }
                                File.WriteAllBytes(target + ".meta", meta);
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add(row.Entry.PathName + ": " + ex.Message);
                        }
                    },
                    onProgress: (pos, total) =>
                    {
                        if (onProgress == null) return true;
                        float f = total > 0 ? (float)pos / total : 0f;
                        if (onProgress(f, "Writing assets...")) return true;
                        cancelled = true;
                        return false;
                    });
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }

            if (cancelled) result.Errors.Add("Cancelled - the project may be half-written.");
            return result;
        }

        /// <summary>
        /// Writes the decisions out as a new .unitypackage instead of importing,
        /// so the result can be inspected, archived, or shared.
        /// </summary>
        public static Result ExportPackage(string packagePath, string outputPath,
                                           List<UpkgRow> rows, UpkgProject project,
                                           Func<float, string, bool> onProgress = null)
        {
            var result = new Result();
            var map = BuildGuidMap(rows, project);

            var byGuid = new Dictionary<string, UpkgRow>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                byGuid[r.Entry.Guid] = r;
                if (r.Action == UpkgAction.Skip)
                {
                    result.Skipped++;
                    if (!string.IsNullOrEmpty(r.RedirectTo)) result.Redirected++;
                }
                else if (r.NewGuid != r.Entry.Guid) result.Remapped++;
            }

            using (var writer = new UpkgArchive.Writer(outputPath))
            {
                UpkgArchive.Read(packagePath,
                    want: m =>
                    {
                        UpkgRow row;
                        return byGuid.TryGetValue(m.Guid, out row) &&
                               row.Action != UpkgAction.Skip;
                    },
                    onPayload: (m, data) =>
                    {
                        var row = byGuid[m.Guid];

                        if (m.Name == "pathname")
                        {
                            var text = row.Entry.PathName;
                            if (!string.IsNullOrEmpty(row.Entry.PathNameExtra))
                                text += "\n" + row.Entry.PathNameExtra;
                            data = Encoding.UTF8.GetBytes(text);
                            result.Imported++;
                        }
                        else if (m.Name == "asset.meta")
                        {
                            int hits;
                            data = PatchMeta(data, row.NewGuid, map, out hits);
                            result.ReferencesPatched += hits;
                        }
                        else if (m.Name == "asset" && ShouldPatchPayload(row.Entry))
                        {
                            int hits;
                            data = UpkgText.Substitute(data, map, out hits);
                            result.ReferencesPatched += hits;
                        }

                        writer.Add(row.NewGuid, m.Name, data);
                    },
                    onProgress: (pos, total) =>
                    {
                        if (onProgress == null) return true;
                        float f = total > 0 ? (float)pos / total : 0f;
                        return onProgress(f, "Writing package...");
                    });
            }
            return result;
        }
    }
}
