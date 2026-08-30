using System;
using System.Collections.Generic;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// The package's paths as a folder tree, with each folder carrying the totals
    /// of everything beneath it, so a collision deep in the package is visible from
    /// the top without expanding anything.
    /// </summary>
    public static class UpkgTree
    {
        public class Node
        {
            public string Name;                 // "Animations"
            public string Path;                 // "Assets/GoGo/Animations"
            public UpkgRow Row;                 // set on leaves only
            public List<Node> Children;

            // A folder node also carries a Row when the package ships an entry for
            // the folder itself, so "is a file" has to mean "has no children list".
            public bool IsLeaf { get { return Children == null; } }

            // Totals for this node and everything under it.
            public int Total;
            public int Conflicts;
            public int Selected;
            public int Code;
            public int CodeSelected;
            public int Stolen;

            public bool AllSelected { get { return Total > 0 && Selected == Total; } }
            public bool NoneSelected { get { return Selected == 0; } }
            public bool Mixed { get { return !AllSelected && !NoneSelected; } }
        }

        public static Node Build(IEnumerable<UpkgRow> rows)
        {
            var root = new Node { Name = "", Path = "", Children = new List<Node>() };
            var index = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            index[""] = root;

            foreach (var row in rows)
            {
                var path = row.Entry.PathName;
                var parts = path.Split('/');

                var parent = root;
                var soFar = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    soFar = i == 0 ? parts[0] : $"{soFar}/{parts[i]}";
                    bool leaf = i == parts.Length - 1;

                    // A folder entry in the package is still a folder in the tree; its
                    // row rides along on the node rather than becoming a leaf of its own.
                    if (leaf && row.Entry.IsFolder)
                    {
                        Node existingFolder;
                        if (!index.TryGetValue(soFar, out existingFolder))
                        {
                            existingFolder = new Node
                            {
                                Name = parts[i],
                                Path = soFar,
                                Children = new List<Node>(),
                            };
                            index[soFar] = existingFolder;
                            parent.Children.Add(existingFolder);
                        }
                        existingFolder.Row = existingFolder.Row ?? row;
                        break;
                    }

                    if (leaf)
                    {
                        parent.Children.Add(new Node
                        {
                            Name = parts[i],
                            Path = soFar,
                            Row = row,
                        });
                        break;
                    }

                    Node next;
                    if (!index.TryGetValue(soFar, out next))
                    {
                        next = new Node
                        {
                            Name = parts[i],
                            Path = soFar,
                            Children = new List<Node>(),
                        };
                        index[soFar] = next;
                        parent.Children.Add(next);
                    }
                    if (next.Children == null) next.Children = new List<Node>();
                    parent = next;
                }
            }

            Collapse(root);
            Sort(root);
            return root;
        }

        /// <summary>
        /// Folds runs of single-child folders into one row ("Assets/GoGo/GoLoco"),
        /// so deep packages do not cost a click per level to reach anything.
        /// </summary>
        static void Collapse(Node node)
        {
            if (node.Children == null) return;

            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                while (child.Children != null && child.Children.Count == 1 &&
                       child.Row == null && !child.Children[0].IsLeaf)
                {
                    var only = child.Children[0];
                    only.Name = $"{child.Name}/{only.Name}";
                    node.Children[i] = only;
                    child = only;
                }
                Collapse(child);
            }
        }

        static void Sort(Node node)
        {
            if (node.Children == null) return;
            node.Children.Sort((a, b) =>
            {
                bool af = !a.IsLeaf, bf = !b.IsLeaf;
                if (af != bf) return af ? -1 : 1;      // folders first
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var child in node.Children) Sort(child);
        }

        /// <summary>Recomputes every total. Cheap enough to run whenever a box is ticked.</summary>
        public static void Recount(Node node)
        {
            node.Total = node.Conflicts = node.Selected = 0;
            node.Code = node.CodeSelected = node.Stolen = 0;

            if (node.IsLeaf)
            {
                Tally(node, node.Row);
                return;
            }

            if (node.Row != null) Tally(node, node.Row);

            if (node.Children == null) return;
            foreach (var child in node.Children)
            {
                Recount(child);
                node.Total += child.Total;
                node.Conflicts += child.Conflicts;
                node.Selected += child.Selected;
                node.Code += child.Code;
                node.CodeSelected += child.CodeSelected;
                node.Stolen += child.Stolen;
            }
        }

        static void Tally(Node node, UpkgRow row)
        {
            node.Total++;
            if (row.IsConflict) node.Conflicts++;
            if (row.Verdict == UpkgVerdict.GuidStolen) node.Stolen++;
            if (row.Action != UpkgAction.Skip) node.Selected++;
            if (UpkgScriptAudit.IsCode(row.Entry))
            {
                node.Code++;
                if (row.Action != UpkgAction.Skip) node.CodeSelected++;
            }
        }

        /// <summary>Applies an action to every row under a node.</summary>
        public static void SetAll(Node node, UpkgAction action, bool allowCode)
        {
            if (node.Row != null && (allowCode || !UpkgScriptAudit.IsCode(node.Row.Entry)))
                node.Row.Action = action;

            if (node.Children == null) return;
            foreach (var child in node.Children) SetAll(child, action, allowCode);
        }

        public static IEnumerable<Node> Walk(Node node)
        {
            yield return node;
            if (node.Children == null) yield break;
            foreach (var child in node.Children)
                foreach (var n in Walk(child))
                    yield return n;
        }
    }
}
