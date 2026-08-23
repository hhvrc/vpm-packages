using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Inspect a .unitypackage against this project before importing it, decide
    /// what comes in, and import with colliding guids remapped.
    ///
    /// The hazard this exists for: a package claiming a guid that already belongs
    /// to a different avatar. The paths differ, so Unity's own import dialog looks
    /// completely clean while existing references get silently re-pointed.
    ///
    /// Layout lives in UpkgWindow.uxml / .uss; this file only wires it up.
    /// </summary>
    public class UpkgWindow : EditorWindow
    {
        [MenuItem("Tools/HeavenVR/ImportGuard")]
        public static void Open()
        {
            var window = GetWindow<UpkgWindow>("Import Guard");
            window.minSize = new Vector2(760, 520);
            window.Show();
        }

        /// <summary>
        /// Opens the window already pointed at a package. Used when a double-click
        /// on a .unitypackage is intercepted before Unity's importer sees it.
        /// </summary>
        public static void OpenWith(string packagePath)
        {
            var window = GetWindow<UpkgWindow>("Import Guard");
            window.minSize = new Vector2(760, 520);
            window.Show();
            window.Focus();

            // CreateGUI has not necessarily run yet on a freshly opened window.
            window._queuedPackage = packagePath;
            if (window._treeView != null) window.ConsumeQueuedPackage();
        }

        string _queuedPackage;

        void ConsumeQueuedPackage()
        {
            var path = _queuedPackage;
            _queuedPackage = null;
            if (!string.IsNullOrEmpty(path)) Scan(path);
        }

        // ---- state ---------------------------------------------------

        string _packagePath = "";
        List<UpkgRow> _rows;
        UpkgProject _project;
        UpkgReferences.Graph _refGraph;
        List<UpkgReferences.Problem> _dangling;

        List<UpkgScriptAudit.ScriptReport> _scripts;
        UpkgScriptAudit.Capabilities _capabilities;
        bool _allowCode;              // deliberately not remembered between packages
        bool _trustAuthor;            // required before compiled binaries may import

        UpkgTree.Node _tree;
        readonly List<UpkgTree.Node> _flat = new List<UpkgTree.Node>();
        readonly Dictionary<int, UpkgTree.Node> _byId = new Dictionary<int, UpkgTree.Node>();
        bool _conflictsOnly = true;
        string _search = "";

        // ---- element handles -----------------------------------------

        VisualElement _emptyState, _report, _summary, _codePanel, _danglingPanel;
        VisualElement _chips, _blastList, _abilities, _domains, _binaries;
        VisualElement _perFileList, _danglingList, _binariesBlock, _filters, _footer;
        Label _packageName, _status, _summaryBanner, _codeBanner, _summaryTitle;
        Label _danglingBanner, _planSummary, _codeBlocked, _abilitiesTitle, _riskPill;
        VisualElement _unreadable, _summaryDot, _riskAccent;
        Toggle _allowCodeToggle, _trustToggle, _conflictsToggle;
        Foldout _domainsFoldout, _perFile, _blastFoldout;
        TreeView _treeView;

        // ---- construction --------------------------------------------

        public void CreateGUI()
        {
            var uxml = LoadTree();
            if (uxml == null)
            {
                rootVisualElement.Add(new Label(
                    "UpkgWindow.uxml could not be found next to UpkgWindow.cs."));
                return;
            }
            uxml.CloneTree(rootVisualElement);

            VisualElement R(string name) { return rootVisualElement.Q(name); }

            _emptyState = R("empty-state");
            _report = R("report");
            _summary = R("summary");
            _codePanel = R("code");
            _danglingPanel = R("dangling");
            _chips = R("chips");
            _blastList = R("blast-list");
            _abilities = R("abilities");
            _domains = R("domains");
            _binaries = R("binaries");
            _binariesBlock = R("binaries-block");
            _perFileList = R("per-file-list");
            _danglingList = R("dangling-list");
            _filters = R("filters");
            _footer = R("footer");

            _packageName = rootVisualElement.Q<Label>("package-name");
            _status = rootVisualElement.Q<Label>("status");
            _summaryBanner = rootVisualElement.Q<Label>("summary-banner");
            _codeBanner = rootVisualElement.Q<Label>("code-banner");
            _danglingBanner = rootVisualElement.Q<Label>("dangling-banner");
            _planSummary = rootVisualElement.Q<Label>("plan-summary");
            _codeBlocked = rootVisualElement.Q<Label>("code-blocked");
            _abilitiesTitle = rootVisualElement.Q<Label>("abilities-title");
            _summaryTitle = rootVisualElement.Q<Label>("summary-title");
            _riskPill = rootVisualElement.Q<Label>("risk-pill");
            _unreadable = R("unreadable");
            _summaryDot = R("summary-dot");
            _riskAccent = R("risk-accent");

            _allowCodeToggle = rootVisualElement.Q<Toggle>("allow-code");
            _trustToggle = rootVisualElement.Q<Toggle>("trust");
            _conflictsToggle = rootVisualElement.Q<Toggle>("conflicts-only");
            _domainsFoldout = rootVisualElement.Q<Foldout>("domains-foldout");
            _perFile = rootVisualElement.Q<Foldout>("per-file");
            _blastFoldout = rootVisualElement.Q<Foldout>("blast-foldout");

            rootVisualElement.Q<ToolbarButton>("open").clicked += ChoosePackage;
            rootVisualElement.Q<ToolbarButton>("rescan").clicked += () =>
            {
                if (!string.IsNullOrEmpty(_packagePath)) Scan(_packagePath);
            };
            rootVisualElement.Q<Button>("expand-all").clicked += () => ExpandAll(true);
            rootVisualElement.Q<Button>("collapse-all").clicked += () => ExpandAll(false);
            rootVisualElement.Q<Button>("import").clicked += DoImport;
            rootVisualElement.Q<Button>("export").clicked += DoExport;
            rootVisualElement.Q<Button>("unity-import").clicked += DoUnityImport;

            _conflictsToggle.RegisterValueChangedCallback(e =>
            {
                _conflictsOnly = e.newValue;
                RebuildTree();
            });

            var search = rootVisualElement.Q<ToolbarSearchField>("search");
            if (search != null)
                search.RegisterValueChangedCallback(e =>
                {
                    _search = e.newValue ?? "";
                    RebuildTree();
                });

            _trustToggle.RegisterValueChangedCallback(e =>
            {
                _trustAuthor = e.newValue;
                RefreshCodePanel();
            });
            _allowCodeToggle.RegisterValueChangedCallback(OnAllowCodeChanged);

            BuildTreeView();
            ShowLoaded(false);
            ConsumeQueuedPackage();
        }

        /// <summary>
        /// Finds the layout next to this script, so the tool works wherever it is
        /// dropped in a project and nothing has to be wired up by hand.
        /// </summary>
        static VisualTreeAsset LoadTree()
        {
            foreach (var guid in AssetDatabase.FindAssets("UpkgWindow t:VisualTreeAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("UpkgWindow.uxml", StringComparison.OrdinalIgnoreCase))
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
                selectionType = SelectionType.Single,
                makeItem = MakeTreeRow,
                bindItem = BindTreeRow,
            };
            _treeView.style.flexGrow = 1f;
            host.Add(_treeView);
        }

        void ShowLoaded(bool loaded)
        {
            _emptyState.EnableInClassList("hidden", loaded);
            foreach (var e in new[] { _report, _filters, _footer })
                e.EnableInClassList("hidden", !loaded);
            rootVisualElement.Q("tree-host").EnableInClassList("hidden", !loaded);
        }

        // ---- loading -------------------------------------------------

        void ChoosePackage()
        {
            var path = EditorUtility.OpenFilePanel("Select a .unitypackage", "", "unitypackage");
            if (!string.IsNullOrEmpty(path)) Scan(path);
        }

        void Scan(string path)
        {
            _packagePath = path;
            _refGraph = null;
            _dangling = null;
            _scripts = null;
            _capabilities = null;
            _allowCode = false;          // every package starts from "no code"
            _trustAuthor = false;

            try
            {
                EditorUtility.DisplayProgressBar("Import Guard", "Indexing project...", 0f);
                _project = UpkgProject.Build();

                var entries = UpkgAnalyzer.Scan(path, f =>
                    EditorUtility.DisplayProgressBar("Import Guard", "Reading package...", f));

                _rows = UpkgAnalyzer.Analyze(entries, _project);
                ApplyDefaultDecisions();

                // Code is read up front: you should not have to ask to find out
                // that a package wants to run something.
                _scripts = UpkgScriptAudit.Audit(path, _rows, f =>
                {
                    EditorUtility.DisplayProgressBar("Import Guard", "Reading code...", f);
                    return true;
                });
                _capabilities = UpkgScriptAudit.Describe(_scripts);

                _packageName.text = Path.GetFileName(path);
                _status.text = string.Format("{0} entries, {1} project guids",
                                             _rows.Count, _project.Count);

                _allowCodeToggle.SetValueWithoutNotify(false);
                _trustToggle.SetValueWithoutNotify(false);

                ShowLoaded(true);
                RebuildTree();
                RefreshSummary();
                RefreshCodePanel();
                RefreshDanglingPanel();
                LogTechnicalReport();
            }
            catch (Exception ex)
            {
                _rows = null;
                _status.text = "failed";
                ShowLoaded(false);
                EditorUtility.DisplayDialog("Import Guard",
                    "Could not read that package:\n\n" + ex.Message, "OK");
                Debug.LogException(ex);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// The window stays in plain language; the exact guids, paths, rule names and
        /// line numbers go to the Console for anyone who wants to dig.
        /// </summary>
        void LogTechnicalReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[Import Guard] ").Append(Path.GetFileName(_packagePath))
              .Append("\n  ").Append(_rows.Count).Append(" entries, ")
              .Append(_project.Count).Append(" guids indexed in project");

            var conflicts = _rows.Where(r => r.IsConflict).ToList();
            if (conflicts.Count > 0)
            {
                sb.Append("\n\n  GUID CONFLICTS (").Append(conflicts.Count).Append(')');
                foreach (var row in conflicts.Take(200))
                {
                    sb.Append("\n    ").Append(row.Verdict).Append("  ")
                      .Append(row.Entry.Guid).Append("  ").Append(row.Entry.PathName);
                    if (!string.IsNullOrEmpty(row.ProjectPath))
                        sb.Append("\n        project: ").Append(row.ProjectPath);
                }
                if (conflicts.Count > 200)
                    sb.Append("\n    ... ").Append(conflicts.Count - 200).Append(" more");
            }

            if (_scripts != null && _scripts.Count > 0)
            {
                sb.Append("\n\n  SCRIPTS (").Append(_scripts.Count).Append(')');
                foreach (var report in _scripts)
                {
                    sb.Append("\n    ").Append(report.Row.Entry.PathName);
                    if (report.IsBinary) { sb.Append("   [compiled, not readable]"); continue; }
                    foreach (var finding in report.Findings)
                        sb.Append("\n        ").Append(finding.Rule.Severity).Append(' ')
                          .Append(finding.Rule.Id)
                          .Append(finding.Line > 0 ? "  line " + finding.Line : "")
                          .Append("  ").Append(finding.Rule.What);
                    foreach (var url in report.Urls) sb.Append("\n        url  ").Append(url);
                }
            }

            if (conflicts.Count > 0) Debug.LogWarning(sb.ToString());
            else Debug.Log(sb.ToString());
        }

        /// <summary>
        /// Start from the safe choice: anything the project already owns a copy of
        /// defers to the project, code stays out, everything else comes in.
        /// </summary>
        void ApplyDefaultDecisions()
        {
            foreach (var row in _rows)
            {
                row.RedirectTo = null;

                if (UpkgScriptAudit.IsCode(row.Entry))
                {
                    row.Action = UpkgAction.Skip;
                    continue;
                }

                row.Action = row.Verdict == UpkgVerdict.GuidStolen &&
                             _project.HasGuid(row.Entry.Guid) &&
                             SameFileName(row)
                    ? UpkgAction.Skip
                    : UpkgAction.Import;
            }
        }

        /// <summary>
        /// A guid collision where the file names also match is almost always the
        /// package shipping its own copy of something you already have.
        /// </summary>
        static bool SameFileName(UpkgRow row)
        {
            if (string.IsNullOrEmpty(row.ProjectPath)) return false;
            return string.Equals(Path.GetFileName(row.ProjectPath),
                                 Path.GetFileName(row.Entry.PathName),
                                 StringComparison.OrdinalIgnoreCase);
        }

        // ---- summary -------------------------------------------------

        IEnumerable<UpkgRow> Visible()
        {
            foreach (var row in _rows)
            {
                if (_conflictsOnly && !row.IsConflict) continue;
                if (!string.IsNullOrEmpty(_search) &&
                    row.Entry.PathName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                yield return row;
            }
        }

        void RefreshSummary()
        {
            var counts = new Dictionary<UpkgVerdict, int>();
            foreach (UpkgVerdict v in Enum.GetValues(typeof(UpkgVerdict))) counts[v] = 0;

            var blast = new Dictionary<string, int>();
            int live = 0;
            foreach (var row in _rows)
            {
                counts[row.Verdict]++;
                bool steals = row.Action == UpkgAction.ImportKeepGuid &&
                              (row.Verdict == UpkgVerdict.GuidStolen ||
                               row.Verdict == UpkgVerdict.PathHijack);
                if (!steals || string.IsNullOrEmpty(row.ProjectPath)) continue;

                live++;
                var parts = row.ProjectPath.Split('/');
                var key = string.Join("/", parts.Take(Math.Min(3, parts.Length)).ToArray());
                int n;
                blast[key] = blast.TryGetValue(key, out n) ? n + 1 : 1;
            }

            _summaryTitle.text = live > 0
                ? "Your existing files would be taken over"
                : "Your existing files are safe";
            _summaryBanner.text = live > 0
                ? string.Format(
                    "This package would take over {0} file(s) you already have. Anything " +
                    "using them would quietly switch to this package's versions instead, " +
                    "and Unity would not warn you.", live)
                : "Nothing you already have will be changed by these choices.";

            SetSeverity(_summary, _summaryDot, live > 0 ? Severity.Danger : Severity.Calm);
            RefreshRiskHeader();

            _chips.Clear();
            AddChip(counts[UpkgVerdict.GuidStolen] + " would take over your files",
                    counts[UpkgVerdict.GuidStolen] > 0);
            AddChip(counts[UpkgVerdict.PathHijack] + " would overwrite your files",
                    counts[UpkgVerdict.PathHijack] > 0);
            AddChip(counts[UpkgVerdict.Duplicate] + " listed twice", false);
            AddChip(counts[UpkgVerdict.Update] + " update what you have", false);
            AddChip(counts[UpkgVerdict.New] + " new", false);

            _blastList.Clear();
            foreach (var kv in blast.OrderByDescending(k => k.Value).Take(10))
                _blastList.Add(Bullet(kv.Key + "   " + kv.Value, true));
            _blastFoldout.EnableInClassList("hidden", blast.Count == 0);

            RefreshFooter();
        }

        void AddChip(string text, bool bad)
        {
            var label = new Label(text);
            label.AddToClassList("chip");
            if (bad) label.AddToClassList("chip--bad");
            _chips.Add(label);
        }

        enum Severity { Calm, Caution, Danger }

        /// <summary>Colours a card's edge and its dot from one decision.</summary>
        static void SetSeverity(VisualElement card, VisualElement dot, Severity severity)
        {
            if (card != null)
            {
                card.EnableInClassList("card--danger", severity == Severity.Danger);
                card.EnableInClassList("card--caution", severity == Severity.Caution);
                card.EnableInClassList("card--calm", severity == Severity.Calm);
            }
            if (dot == null) return;
            dot.EnableInClassList("dot--danger", severity == Severity.Danger);
            dot.EnableInClassList("dot--caution", severity == Severity.Caution);
            dot.EnableInClassList("dot--calm", severity == Severity.Calm);
        }

        /// <summary>The title bar reflects the worst thing found, at a glance.</summary>
        void RefreshRiskHeader()
        {
            bool takeover = _rows != null && _rows.Any(
                r => r.Action == UpkgAction.ImportKeepGuid &&
                     (r.Verdict == UpkgVerdict.GuidStolen ||
                      r.Verdict == UpkgVerdict.PathHijack));
            bool scripts = _capabilities != null && _capabilities.TotalCode > 0;
            bool scriptsComingIn = scripts && _allowCode;

            Severity severity;
            string text;
            if (takeover) { severity = Severity.Danger; text = "Your files would be changed"; }
            else if (scriptsComingIn) { severity = Severity.Danger; text = "Scripts will run"; }
            else if (scripts) { severity = Severity.Caution; text = "Contains scripts"; }
            else { severity = Severity.Calm; text = "Nothing to worry about"; }

            _riskPill.text = text;
            _riskPill.EnableInClassList("pill--danger", severity == Severity.Danger);
            _riskPill.EnableInClassList("pill--caution", severity == Severity.Caution);
            _riskPill.EnableInClassList("pill--calm", severity == Severity.Calm);

            _riskAccent.EnableInClassList("titlebar__accent--danger", severity == Severity.Danger);
            _riskAccent.EnableInClassList("titlebar__accent--caution", severity == Severity.Caution);
            _riskAccent.EnableInClassList("titlebar__accent--calm", severity == Severity.Calm);
        }

        /// <summary>A capability line: severity dot plus wrapping text, hoverable.</summary>
        static VisualElement AbilityRow(string text, bool bad = true)
        {
            var row = new VisualElement();
            row.AddToClassList("ability");
            if (!bad) row.AddToClassList("ability--calm");

            var dot = new VisualElement();
            dot.AddToClassList("dot");
            dot.AddToClassList(bad ? "dot--danger" : "dot--calm");
            row.Add(dot);

            var label = new Label(text);
            label.AddToClassList("ability__text");
            row.Add(label);
            return row;
        }

        static Label Bullet(string text, bool plain = false)
        {
            var label = new Label(plain ? text : "•  " + text);
            label.AddToClassList("url");
            return label;
        }

        void RefreshFooter()
        {
            int importing = _rows.Count(r => r.Action != UpkgAction.Skip);
            int remapping = _rows.Count(r => r.Action == UpkgAction.Import && r.IsConflict);
            _planSummary.text = string.Format(
                "{0} of {1} entries will be imported, {2} with a fresh guid.",
                importing, _rows.Count, remapping);
        }

        // ---- code panel ----------------------------------------------

        void OnAllowCodeChanged(ChangeEvent<bool> e)
        {
            var caps = _capabilities;
            if (e.newValue && caps != null && !caps.Quiet &&
                !EditorUtility.DisplayDialog("Import Guard", ConsentText(caps),
                                             "I understand, import it", "Cancel"))
            {
                _allowCodeToggle.SetValueWithoutNotify(false);
                _allowCode = false;
                return;
            }

            _allowCode = e.newValue;
            foreach (var row in _rows)
                if (UpkgScriptAudit.IsCode(row.Entry))
                    row.Action = _allowCode ? UpkgAction.Import : UpkgAction.Skip;

            RefreshDangling();
            RebuildTree();
            RefreshSummary();
            RefreshDanglingPanel();
        }

        static string ConsentText(UpkgScriptAudit.Capabilities caps)
        {
            var sb = new System.Text.StringBuilder();

            var when = caps.WhenItRuns();
            if (when != null) sb.Append(when).Append("\n\n");
            if (caps.Unreadable)
                sb.Append("Part of this package cannot be read, so there is no way to " +
                          "know what it does until it runs.\n\n");

            if (caps.Abilities.Count > 0)
            {
                sb.Append("Once running, these scripts are able to:\n");
                foreach (var ability in caps.Abilities) sb.Append("\n  - " + ability);
                sb.Append("\n\n");
            }

            if (caps.Domains.Count > 0)
            {
                sb.Append("They mention these internet addresses: ");
                sb.Append(string.Join(", ", caps.Domains.Keys.Take(6).ToArray()));
                if (caps.Domains.Count > 6)
                    sb.Append(", and " + (caps.Domains.Count - 6) + " more");
                sb.Append("\n\n");
            }

            sb.Append("Only continue if you trust whoever made this package.");
            return sb.ToString();
        }

        void RefreshCodePanel()
        {
            var caps = _capabilities;
            bool any = caps != null && caps.TotalCode > 0;
            _codePanel.EnableInClassList("hidden", !any);
            if (!any) return;

            // No counts: a single script can do as much damage as a thousand, and a
            // small number reads as reassurance it has not earned.
            var when = caps.WhenItRuns();
            _codeBanner.text = when
                ?? "This package contains scripts. Scripts are programs that Unity will " +
                   "run on your computer.";

            _unreadable.EnableInClassList("hidden", !caps.Unreadable);

            _abilities.Clear();
            if (caps.Quiet)
            {
                _abilities.Add(AbilityRow(
                    "Nothing in these scripts matched the things this tool checks for. " +
                    "That is not the same as safe.", false));
            }
            else
            {
                foreach (var ability in caps.Abilities) _abilities.Add(AbilityRow(ability));
                if (caps.ObfuscatedStrings > 0)
                    _abilities.Add(AbilityRow(
                        "Carry hidden text that cannot be read by looking at it"));
            }
            _abilitiesTitle.text = caps.Quiet ? "WHAT WE CHECKED" : "THESE SCRIPTS ARE ABLE TO";

            SetSeverity(_codePanel, null,
                        caps.Quiet && !caps.Unreadable ? Severity.Caution : Severity.Danger);

            // One row per registrable domain; the exact URLs live inside it.
            _domains.Clear();
            foreach (var pair in caps.Domains)
            {
                var foldout = new Foldout
                {
                    text = pair.Key + "   (" + pair.Value.Count + ")",
                    value = false,
                };
                foldout.AddToClassList("domain-foldout");
                foreach (var url in pair.Value)
                {
                    var label = new Label(url) { selection = { isSelectable = true } };
                    label.AddToClassList("url");
                    foldout.Add(label);
                }
                _domains.Add(foldout);
            }
            _domainsFoldout.text = string.Format("Addresses it contains ({0} domain(s))",
                                                 caps.Domains.Count);
            _domainsFoldout.EnableInClassList("hidden", caps.Domains.Count == 0);

            _binariesBlock.EnableInClassList("hidden", !caps.Unreadable);
            if (caps.Unreadable)
            {
                _binaries.Clear();
                foreach (var binary in caps.Binaries) _binaries.Add(Bad(Bullet(binary)));
            }

            bool blocked = caps.Unreadable && !_trustAuthor;
            _allowCodeToggle.SetEnabled(!blocked);
            _codeBlocked.EnableInClassList("hidden", !blocked);

            _perFileList.Clear();
            foreach (var report in _scripts) _perFileList.Add(PerFileRow(report));
        }

        static Label Bad(Label label)
        {
            label.AddToClassList("bad");
            return label;
        }

        VisualElement PerFileRow(UpkgScriptAudit.ScriptReport report)
        {
            var container = new VisualElement();
            var title = report.Row.Entry.PathName +
                        (report.IsBinary
                            ? "   [compiled - cannot be reviewed]"
                            : report.Findings.Count == 0
                                ? "   [nothing matched]"
                                : "   [" + report.Findings.Count + " finding(s)]");

            var foldout = new Foldout { text = title, value = false };
            foreach (var finding in report.Findings)
            {
                var line = finding.Line > 0 ? "  (line " + finding.Line + ")" : "";
                var label = new Label(finding.Rule.Severity.ToString().ToUpperInvariant() +
                                      "  " + finding.Rule.What + line)
                {
                    tooltip = finding.Rule.Why,
                };
                label.AddToClassList("url");
                if (finding.Rule.Severity == UpkgScriptAudit.Severity.High)
                    label.AddToClassList("bad");
                foldout.Add(label);

                if (!string.IsNullOrEmpty(finding.Text))
                {
                    var code = new Label(finding.Text) { selection = { isSelectable = true } };
                    code.AddToClassList("url");
                    foldout.Add(code);
                }
            }

            if (!report.IsBinary && !string.IsNullOrEmpty(report.Source))
            {
                var view = new Button(() => ShowSource(report)) { text = "View source" };
                foldout.Add(view);
            }

            container.Add(foldout);
            return container;
        }

        static void ShowSource(UpkgScriptAudit.ScriptReport report)
        {
            var window = CreateInstance<UpkgSourceWindow>();
            window.titleContent = new GUIContent(Path.GetFileName(report.Row.Entry.PathName));
            window.Source = report.Source;
            window.Show();
        }

        // ---- dangling references -------------------------------------

        void RefreshDangling()
        {
            if (_refGraph == null) return;
            _dangling = UpkgReferences.FindDanglingSkips(_rows, _refGraph, _project);
        }

        void RefreshDanglingPanel()
        {
            bool any = _dangling != null && _dangling.Count > 0;
            _danglingPanel.EnableInClassList("hidden", !any);
            if (!any) return;

            _danglingBanner.text = string.Format(
                "{0} asset(s) you left out are still referenced, and your project has no " +
                "copy. Pick a replacement, or let them import.", _dangling.Count);

            _danglingList.Clear();
            foreach (var problem in _dangling.Take(20))
            {
                var block = new VisualElement();
                block.Add(Bad(Bullet(problem.Row.Entry.PathName)));
                foreach (var needer in problem.NeededBy.Take(3))
                {
                    var label = new Label("needed by: " + needer.Entry.PathName);
                    label.AddToClassList("url");
                    block.Add(label);
                }

                var row = new VisualElement();
                row.AddToClassList("row");

                var field = new ObjectField("Replace with")
                {
                    objectType = typeof(UnityEngine.Object),
                    allowSceneObjects = false,
                };
                var target = problem.Row;
                field.RegisterValueChangedCallback(e =>
                {
                    var path = AssetDatabase.GetAssetPath(e.newValue);
                    target.RedirectTo = string.IsNullOrEmpty(path)
                        ? null
                        : AssetDatabase.AssetPathToGUID(path).ToLowerInvariant();
                    RefreshDangling();
                    RefreshDanglingPanel();
                });
                row.Add(field);

                row.Add(new Button(() =>
                {
                    target.Action = UpkgAction.Import;
                    RefreshDangling();
                    RebuildTree();
                    RefreshSummary();
                    RefreshDanglingPanel();
                })
                { text = "Import it instead" });

                block.Add(row);
                _danglingList.Add(block);
            }
        }

        // ---- tree ----------------------------------------------------

        void RebuildTree()
        {
            if (_rows == null) return;

            _tree = UpkgTree.Build(Visible().ToList());
            UpkgTree.Recount(_tree);

            _flat.Clear();
            _byId.Clear();
            var roots = new List<TreeViewItemData<int>>();
            foreach (var child in _tree.Children ?? new List<UpkgTree.Node>())
                roots.Add(ToItem(child));

            _treeView.SetRootItems(roots);
            _treeView.Rebuild();

            // With a filter on, the surviving set is small and worth showing open.
            if (_conflictsOnly || !string.IsNullOrEmpty(_search)) ExpandAll(true);
        }

        TreeViewItemData<int> ToItem(UpkgTree.Node node)
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

        void ExpandAll(bool open)
        {
            if (_treeView == null) return;
            if (open) _treeView.ExpandAll();
            else _treeView.CollapseAll();
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

            var risk = new Label { name = "risk" };
            risk.AddToClassList("pill");
            risk.AddToClassList("pill--danger");
            row.Add(risk);

            var counts = new Label { name = "counts" };
            counts.AddToClassList("pill");
            row.Add(counts);

            return row;
        }

        void BindTreeRow(VisualElement element, int index)
        {
            var id = _treeView.GetItemDataForIndex<int>(index);
            UpkgTree.Node node;
            if (!_byId.TryGetValue(id, out node)) return;

            var toggle = element.Q<Toggle>("pick");
            var name = element.Q<Label>("name");
            var counts = element.Q<Label>("counts");
            var risk = element.Q<Label>("risk");

            element.EnableInClassList("tree-row--odd", (index & 1) == 1);

            bool isCode = node.IsLeaf && node.Row != null &&
                          UpkgScriptAudit.IsCode(node.Row.Entry);

            toggle.SetValueWithoutNotify(node.Selected > 0);
            toggle.showMixedValue = node.Mixed;
            // Code can always be switched off, but switching it on happens only
            // through the consent toggle above.
            toggle.SetEnabled(!isCode || _allowCode);

            toggle.UnregisterCallback<ChangeEvent<bool>>(OnRowToggled);
            toggle.userData = node;
            toggle.RegisterCallback<ChangeEvent<bool>>(OnRowToggled);

            name.text = node.Name;
            name.EnableInClassList("tree-name--off", node.Selected == 0);
            name.EnableInClassList("tree-name--stolen",
                node.IsLeaf
                    ? node.Row != null && node.Row.Verdict == UpkgVerdict.GuidStolen
                    : node.Stolen > 0);

            name.EnableInClassList("tree-name--folder", !node.IsLeaf);

            if (node.IsLeaf && node.Row != null)
            {
                name.tooltip = node.Row.Entry.PathName + "\n\n" +
                               UpkgAnalyzer.Describe(node.Row.Verdict);

                risk.text = node.Row.Verdict == UpkgVerdict.GuidStolen ? "takes over yours"
                          : node.Row.Verdict == UpkgVerdict.PathHijack ? "overwrites yours"
                          : "";
                counts.text = isCode ? "script" : "";
            }
            else
            {
                name.tooltip = node.Path;
                risk.text = node.Stolen > 0 ? node.Stolen + " take over yours" : "";
                counts.text = node.Code > 0
                    ? string.Format("{0}/{1}  ·  {2} script(s)", node.Selected, node.Total,
                                    node.Code)
                    : string.Format("{0}/{1}", node.Selected, node.Total);
            }

            risk.EnableInClassList("hidden", string.IsNullOrEmpty(risk.text));
            counts.EnableInClassList("hidden", string.IsNullOrEmpty(counts.text));
        }

        void OnRowToggled(ChangeEvent<bool> e)
        {
            var toggle = e.target as Toggle;
            if (toggle == null) return;
            var node = toggle.userData as UpkgTree.Node;
            if (node == null) return;

            UpkgTree.SetAll(node, e.newValue ? UpkgAction.Import : UpkgAction.Skip, _allowCode);
            UpkgTree.Recount(_tree);
            _treeView.RefreshItems();

            RefreshDangling();
            RefreshSummary();
            RefreshDanglingPanel();
        }

        // ---- actions -------------------------------------------------

        void DoImport()
        {
            int live = _rows.Count(r => r.Action == UpkgAction.ImportKeepGuid &&
                                        (r.Verdict == UpkgVerdict.GuidStolen ||
                                         r.Verdict == UpkgVerdict.PathHijack));
            var warning = live > 0
                ? string.Format(
                    "\n\nWARNING: {0} entries are set to keep a guid that already belongs " +
                    "to one of your assets. Those references will be re-pointed.", live)
                : "";

            int code = _rows.Count(r => r.Action != UpkgAction.Skip &&
                                        UpkgScriptAudit.IsCode(r.Entry));
            if (code > 0)
                warning += string.Format(
                    "\n\n{0} of these are code files. They will compile and can run on " +
                    "this machine.", code);

            if (!EditorUtility.DisplayDialog("Import Guard",
                    string.Format("Import {0} entries into this project?{1}",
                        _rows.Count(r => r.Action != UpkgAction.Skip), warning),
                    "Import", "Cancel"))
                return;

            try
            {
                var result = UpkgImporter.ImportIntoProject(
                    _packagePath, _rows, _project,
                    (f, msg) =>
                    {
                        EditorUtility.DisplayProgressBar("Import Guard", msg, f);
                        return true;
                    });

                _status.text = result.ToString();
                Debug.Log("[Import Guard] " + result);
                foreach (var error in result.Errors) Debug.LogError("[Import Guard] " + error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Escape hatch: hand the package to Unity's own dialog. Import Guard sits in
        /// front of every interactive import, so there has to be a way past it.
        /// </summary>
        void DoUnityImport()
        {
            if (!EditorUtility.DisplayDialog("Import Guard",
                    "Hand this package to Unity's own import dialog?\n\n" +
                    "Nothing will be checked: colliding guids will be taken over " +
                    "silently, and any code in the package will compile and run.",
                    "Use Unity's importer", "Cancel"))
                return;

            UpkgImportPatch.ImportWithUnity(_packagePath);
            Close();
        }

        void DoExport()
        {
            var output = EditorUtility.SaveFilePanel("Export rewritten package", "",
                Path.GetFileNameWithoutExtension(_packagePath) + " [guarded]", "unitypackage");
            if (string.IsNullOrEmpty(output)) return;

            try
            {
                var result = UpkgImporter.ExportPackage(
                    _packagePath, output, _rows, _project,
                    (f, msg) =>
                    {
                        EditorUtility.DisplayProgressBar("Import Guard", msg, f);
                        return true;
                    });

                _status.text = result.ToString();
                Debug.Log("[Import Guard] wrote " + output + " - " + result);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }

    /// <summary>Read-only viewer for one script's source.</summary>
    public class UpkgSourceWindow : EditorWindow
    {
        public string Source;

        void CreateGUI()
        {
            var scroll = new ScrollView();
            var text = new TextField { multiline = true, value = Source ?? "" };
            text.SetEnabled(true);
            text.style.whiteSpace = WhiteSpace.Normal;
            scroll.Add(text);
            scroll.style.flexGrow = 1f;
            rootVisualElement.Add(scroll);
        }
    }
}
