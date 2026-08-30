using System;
using System.Collections.Generic;
using UnityEditor;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Reads a package and classifies what importing it would do to the project.
    /// Knows nothing about the UI or about user decisions.
    /// </summary>
    public static class UpkgAnalyzer
    {
        /// <summary>Reads every entry in one pass, without reading asset payloads.</summary>
        public static List<UpkgEntry> Scan(string packagePath, Action<float> onProgress = null)
        {
            var byGuid = new Dictionary<string, UpkgEntry>(StringComparer.Ordinal);

            Func<string, UpkgEntry> get = guid =>
            {
                UpkgEntry entry;
                if (!byGuid.TryGetValue(guid, out entry))
                {
                    entry = new UpkgEntry { Guid = guid };
                    byGuid.Add(guid, entry);
                }
                return entry;
            };

            UpkgArchive.Read(packagePath,
                want: m =>
                {
                    // Payload size and presence come from the header, so `asset` and
                    // the preview are recorded here and their bytes never read.
                    if (m.Name == "asset")
                    {
                        var e = get(m.Guid);
                        e.HasAsset = true;
                        e.AssetSize = m.Size;
                        return false;
                    }
                    if (m.Name == "preview.png")
                    {
                        get(m.Guid).HasPreview = true;
                        return false;
                    }
                    return m.Name == "pathname" || m.Name == "asset.meta";
                },
                onPayload: (m, data) =>
                {
                    var entry = get(m.Guid);
                    if (m.Name == "asset.meta")
                    {
                        entry.Meta = data;
                        return;
                    }

                    var text = System.Text.Encoding.UTF8.GetString(data)
                        .Replace("\r\n", "\n");
                    int nl = text.IndexOf('\n');
                    if (nl < 0)
                    {
                        entry.PathName = text.Trim();
                    }
                    else
                    {
                        entry.PathName = text.Substring(0, nl).Trim();
                        entry.PathNameExtra = text.Substring(nl + 1);
                    }
                },
                onProgress: (pos, total) =>
                {
                    if (onProgress != null) onProgress(total > 0 ? (float)pos / total : 0f);
                    return true;
                });

            var result = new List<UpkgEntry>();
            foreach (var e in byGuid.Values)
                if (!string.IsNullOrEmpty(e.PathName)) result.Add(e);
            return result;
        }

        /// <summary>Classifies each entry against the project.</summary>
        public static List<UpkgRow> Analyze(List<UpkgEntry> entries, UpkgProject project,
                                            Func<UpkgEntry, string> destinationOf = null)
        {
            if (destinationOf == null) destinationOf = e => e.PathName;

            var seenPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var rows = new List<UpkgRow>(entries.Count);

            foreach (var entry in entries)
            {
                var dest = destinationOf(entry);
                var key = UpkgText.Normalize(dest);
                var row = new UpkgRow { Entry = entry };

                string firstOwner;
                if (seenPaths.TryGetValue(key, out firstOwner) && firstOwner != entry.Guid)
                {
                    row.Verdict = UpkgVerdict.Duplicate;
                    row.ProjectPath = dest;
                    rows.Add(row);
                    continue;
                }
                seenPaths[key] = entry.Guid;

                var guidOwner = project.PathOfGuid(entry.Guid);
                var pathOwnerGuid = project.GuidAtPath(dest);

                if (guidOwner != null && UpkgText.Normalize(guidOwner) != key)
                {
                    row.Verdict = UpkgVerdict.GuidStolen;
                    row.ProjectPath = guidOwner;
                    row.ProjectGuid = entry.Guid;
                }
                else if (pathOwnerGuid != null && pathOwnerGuid != entry.Guid)
                {
                    row.Verdict = UpkgVerdict.PathHijack;
                    row.ProjectPath = dest;
                    row.ProjectGuid = pathOwnerGuid;
                }
                else if (pathOwnerGuid != null && pathOwnerGuid == entry.Guid)
                {
                    row.Verdict = UpkgVerdict.Update;
                    row.ProjectPath = dest;
                    row.ProjectGuid = entry.Guid;
                }
                else
                {
                    row.Verdict = UpkgVerdict.New;
                }
                rows.Add(row);
            }

            rows.Sort((a, b) =>
            {
                int c = string.Compare(a.Entry.PathName, b.Entry.PathName,
                                       StringComparison.OrdinalIgnoreCase);
                return c;
            });
            return rows;
        }

        public static string Describe(UpkgVerdict verdict)
        {
            switch (verdict)
            {
                case UpkgVerdict.GuidStolen:
                    return "This guid already belongs to a different asset in your project. Importing makes two files claim one guid and Unity will silently re-point existing references. Unity shows no warning for this.";
                case UpkgVerdict.PathHijack:
                    return "An asset already lives at this path with a different guid. The file is overwritten and every existing reference to it breaks.";
                case UpkgVerdict.Duplicate:
                    return "Two entries inside the package target this same path.";
                case UpkgVerdict.Update:
                    return "Same path and same guid: an ordinary clean update.";
                default:
                    return "No conflict.";
            }
        }
    }
}
