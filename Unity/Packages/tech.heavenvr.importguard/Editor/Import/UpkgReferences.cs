using System;
using System.Collections.Generic;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Works out which entries reference which, so leaving something out can be
    /// reported as "still needed by X" instead of silently breaking a link.
    /// </summary>
    public static class UpkgReferences
    {
        public class Graph
        {
            /// <summary>referenced guid -> guids of the entries that reference it.</summary>
            public readonly Dictionary<string, HashSet<string>> Referrers =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            public HashSet<string> Of(string guid)
            {
                HashSet<string> set;
                return Referrers.TryGetValue(guid, out set) ? set : null;
            }
        }

        const long MaxPayload = 32L * 1024 * 1024;   // no text asset is bigger than this

        public static Graph Scan(string packagePath, List<UpkgEntry> entries,
                                 Func<float, bool> onProgress = null)
        {
            var byGuid = new Dictionary<string, UpkgEntry>(StringComparer.Ordinal);
            foreach (var e in entries) byGuid[e.Guid] = e;

            var graph = new Graph();
            Action<string, byte[]> note = (owner, data) =>
            {
                var text = System.Text.Encoding.GetEncoding(28591).GetString(data);
                foreach (System.Text.RegularExpressions.Match m in
                         UpkgText.GuidPattern.Matches(text))
                {
                    HashSet<string> set;
                    if (!graph.Referrers.TryGetValue(m.Value, out set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        graph.Referrers.Add(m.Value, set);
                    }
                    set.Add(owner);
                }
            };

            UpkgArchive.Read(packagePath,
                want: m =>
                {
                    if (m.Name == "asset.meta") return true;
                    if (m.Name != "asset" || m.Size > MaxPayload) return false;
                    UpkgEntry e;
                    return byGuid.TryGetValue(m.Guid, out e) &&
                           UpkgText.TextAssetExtensions.Contains(e.Extension);
                },
                onPayload: (m, data) => note(m.Guid, data),
                onProgress: (pos, total) =>
                {
                    if (onProgress == null) return true;
                    return onProgress(total > 0 ? (float)pos / total : 0f);
                });

            return graph;
        }

        public class Problem
        {
            public UpkgRow Row;                    // the entry being left out
            public List<UpkgRow> NeededBy = new List<UpkgRow>();
        }

        /// <summary>
        /// Entries the user chose to leave out that something still points at, and
        /// which the project has no copy of. These are the ones worth redirecting.
        /// </summary>
        public static List<Problem> FindDanglingSkips(List<UpkgRow> rows, Graph graph,
                                                      UpkgProject project)
        {
            var byGuid = new Dictionary<string, UpkgRow>(StringComparer.Ordinal);
            foreach (var r in rows) byGuid[r.Entry.Guid] = r;

            var problems = new List<Problem>();
            foreach (var row in rows)
            {
                if (row.Action != UpkgAction.Skip) continue;
                if (!string.IsNullOrEmpty(row.RedirectTo)) continue;   // already handled
                if (project.HasGuid(row.Entry.Guid)) continue;         // project has a copy

                var referrers = graph.Of(row.Entry.Guid);
                if (referrers == null) continue;

                var problem = new Problem { Row = row };
                foreach (var guid in referrers)
                {
                    if (guid == row.Entry.Guid) continue;
                    UpkgRow referrer;
                    if (byGuid.TryGetValue(guid, out referrer) &&
                        referrer.Action != UpkgAction.Skip)
                        problem.NeededBy.Add(referrer);
                }
                if (problem.NeededBy.Count > 0) problems.Add(problem);
            }
            return problems;
        }
    }
}
