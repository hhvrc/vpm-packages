using System;
using System.Collections.Generic;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Checkbox folder tree for the export-selection window. Unlike UpkgTree
    /// (import review: verdicts, conflicts, guid actions), a node here is just
    /// "included or not" - built from live project asset paths, not package
    /// entries read back out of an archive.
    /// </summary>
    public static class UpkgExportTree
    {
        public class Node
        {
            public string Name;
            public string Path;
            public List<Node> Children;  // null => leaf (a real asset file)
            public bool Checked = true;  // meaningful on leaves only

            public bool IsLeaf { get { return Children == null; } }

            public int Total;
            public int Selected;
            public bool AllSelected { get { return Total > 0 && Selected == Total; } }
            public bool NoneSelected { get { return Selected == 0; } }
            public bool Mixed { get { return !AllSelected && !NoneSelected; } }
        }

        public static Node Build(IEnumerable<string> paths)
        {
            var root = new Node { Name = "", Path = "", Children = new List<Node>() };
            var index = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase) { { "", root } };

            foreach (var path in paths)
            {
                var parts = path.Split('/');
                var parent = root;
                var soFar = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    soFar = i == 0 ? parts[0] : $"{soFar}/{parts[i]}";
                    bool leaf = i == parts.Length - 1;

                    Node node;
                    if (!index.TryGetValue(soFar, out node))
                    {
                        node = new Node
                        {
                            Name = parts[i],
                            Path = soFar,
                            Children = leaf ? null : new List<Node>(),
                        };
                        index[soFar] = node;
                        parent.Children.Add(node);
                    }
                    if (!leaf)
                    {
                        if (node.Children == null) node.Children = new List<Node>();
                        parent = node;
                    }
                }
            }

            Collapse(root);
            Sort(root);
            return root;
        }

        /// <summary>Folds runs of single-child folders into one row, same as
        /// UpkgTree - deep project hierarchies otherwise cost a click per level.</summary>
        static void Collapse(Node node)
        {
            if (node.Children == null) return;
            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                while (child.Children != null && child.Children.Count == 1 && !child.Children[0].IsLeaf)
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
                if (af != bf) return af ? -1 : 1;   // folders first
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var child in node.Children) Sort(child);
        }

        public static void Recount(Node node)
        {
            if (node.IsLeaf)
            {
                node.Total = 1;
                node.Selected = node.Checked ? 1 : 0;
                return;
            }
            node.Total = node.Selected = 0;
            foreach (var child in node.Children)
            {
                Recount(child);
                node.Total += child.Total;
                node.Selected += child.Selected;
            }
        }

        public static void SetAll(Node node, bool value)
        {
            if (node.IsLeaf) { node.Checked = value; return; }
            foreach (var child in node.Children) SetAll(child, value);
        }

        public static IEnumerable<Node> Walk(Node node)
        {
            yield return node;
            if (node.Children == null) yield break;
            foreach (var child in node.Children)
                foreach (var n in Walk(child))
                    yield return n;
        }

        public static IEnumerable<string> CheckedPaths(Node root)
        {
            foreach (var n in Walk(root))
                if (n.IsLeaf && n.Checked)
                    yield return n.Path;
        }
    }
}
