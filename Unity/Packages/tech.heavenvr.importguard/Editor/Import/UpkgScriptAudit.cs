using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Finds executable code inside a package and reports patterns worth a human
    /// look before any of it is allowed to compile.
    ///
    /// This is a heuristic reading of source text, not a sandbox and not proof of
    /// anything. It exists because a single Editor script marked InitializeOnLoad
    /// runs the moment Unity finishes importing - before you have clicked anything.
    /// A clean report means "nothing matched these rules", never "this is safe".
    /// </summary>
    public static class UpkgScriptAudit
    {
        public enum Severity { Info, Medium, High }

        public class Rule
        {
            public string Id;
            public Severity Severity;
            public Regex Pattern;
            public string What;      // what the pattern is
            public string Why;       // why it is worth looking at
        }

        public class Finding
        {
            public Rule Rule;
            public int Line;
            public string Text;      // the matching source line, trimmed
        }

        public class ScriptReport
        {
            public UpkgRow Row;
            public string Source;            // null for binaries
            public bool IsBinary;
            public List<Finding> Findings = new List<Finding>();
            public SortedSet<string> Urls =
                new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public int ObfuscatedStrings;

            public Severity Worst
            {
                get
                {
                    var worst = Severity.Info;
                    foreach (var f in Findings)
                        if (f.Rule.Severity > worst) worst = f.Rule.Severity;
                    return worst;
                }
            }
        }

        /// <summary>Extensions that can execute, or that decide what executes.</summary>
        public static readonly HashSet<string> CodeExtensions = new HashSet<string>
        {
            ".cs", ".dll", ".asmdef", ".asmref", ".rsp", ".jslib",
            ".so", ".dylib", ".bundle", ".exe", ".bat", ".cmd", ".ps1", ".sh",
            ".py", ".pyd", ".lua", ".js", ".vbs", ".jar",
        };

        /// <summary>Compiled code that cannot be read, so it cannot be reviewed.</summary>
        public static readonly HashSet<string> BinaryCodeExtensions = new HashSet<string>
        {
            ".dll", ".so", ".dylib", ".bundle", ".exe", ".pyd", ".jar",
        };

        public static bool IsCode(UpkgEntry entry)
        {
            return CodeExtensions.Contains(entry.Extension);
        }

        static Rule R(string id, Severity sev, string pattern, string what, string why)
        {
            return new Rule
            {
                Id = id,
                Severity = sev,
                Pattern = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant),
                What = what,
                Why = why,
            };
        }

        public static readonly List<Rule> Rules = new List<Rule>
        {
            // --- runs without you asking ---------------------------------
            R("auto-run", Severity.High,
              @"\[\s*InitializeOnLoad(Method)?\s*\]|\[\s*DidReloadScripts",
              "runs automatically on import",
              "This code executes as soon as Unity finishes compiling, before you open or click anything. Everything else this script does happens by itself."),
            R("auto-run-runtime", Severity.Medium,
              @"\[\s*RuntimeInitializeOnLoadMethod",
              "runs automatically when the game starts",
              "Executes on play without being attached to any object."),

            // --- reaching outside Unity ----------------------------------
            R("process", Severity.High,
              @"Process\s*\.\s*Start|ProcessStartInfo|System\s*\.\s*Diagnostics\s*\.\s*Process",
              "launches another program",
              "Starts an external executable or shell command."),
            R("native", Severity.High,
              @"\[\s*DllImport",
              "calls native code",
              "Invokes functions in a native library, outside anything Unity checks."),
            R("registry", Severity.High,
              @"Microsoft\s*\.\s*Win32\s*\.\s*Registry|RegistryKey",
              "reads or writes the Windows registry",
              "Common way to persist across reinstalls."),

            // --- network -------------------------------------------------
            R("network", Severity.High,
              @"UnityWebRequest|HttpClient|WebClient|HttpWebRequest|TcpClient|UdpClient|Socket\s*\(",
              "makes network requests",
              "Can send project contents out, or pull further code in."),
            R("webhook", Severity.High,
              @"discord\.com/api/webhooks|hooks\.slack\.com|api\.telegram\.org",
              "contains a chat webhook URL",
              "Webhooks are the usual way exfiltrated data is delivered."),
            R("url", Severity.Info,
              @"https?://[^\s""']+",
              "contains a URL",
              "Worth a glance to see where it points."),
            R("openurl", Severity.Medium,
              @"Application\s*\.\s*OpenURL",
              "opens a web page",
              "Opens the user's browser at a chosen address."),

            // --- loading more code at runtime -----------------------------
            R("assembly-load", Severity.High,
              @"Assembly\s*\.\s*Load|AppDomain\s*\.\s*CurrentDomain\s*\.\s*Load",
              "loads code at runtime",
              "Code loaded this way never appears in the project and cannot be reviewed."),
            R("compile", Severity.High,
              @"CSharpCodeProvider|CompileAssemblyFromSource|CodeDomProvider",
              "compiles code at runtime",
              "Builds and runs new code while Unity is open."),
            R("reflection-invoke", Severity.Medium,
              @"GetMethod\s*\(|Invoke\s*\(\s*null|BindingFlags\s*\.\s*NonPublic",
              "calls methods by name via reflection",
              "Legitimate in editor tools, but also the standard way to hide what is called."),

            // --- obfuscation ---------------------------------------------
            R("base64", Severity.High,
              @"Convert\s*\.\s*FromBase64String",
              "decodes base64 data",
              "Frequently how a hidden payload is carried past a reader."),
            R("long-literal", Severity.Medium,
              @"""[A-Za-z0-9+/=]{200,}""",
              "contains a very long encoded string",
              "Large opaque literals are unusual in ordinary avatar scripts."),
            R("escaped-string", Severity.Medium,
              @"(\\u00[0-9a-fA-F]{2}){6,}",
              "contains an escaped string",
              "Text written this way is hidden from anyone skimming the file."),

            // --- destructive / persistence -------------------------------
            R("delete", Severity.High,
              @"Directory\s*\.\s*Delete|File\s*\.\s*Delete|AssetDatabase\s*\.\s*DeleteAsset|FileUtil\s*\.\s*Delete",
              "deletes files",
              "Check what it targets - a wrong path here removes your work."),
            R("write-outside", Severity.Medium,
              @"Environment\s*\.\s*GetFolderPath|Environment\s*\.\s*SpecialFolder|%APPDATA%|AppData",
              "touches folders outside the project",
              "Reaches into user directories rather than the project."),
            R("editor-hook", Severity.Medium,
              @"EditorApplication\s*\.\s*(update|delayCall|playModeStateChanged)\s*\+=",
              "hooks into the editor loop",
              "Keeps running in the background for as long as the project is open."),
            R("file-write", Severity.Info,
              @"File\s*\.\s*Write|StreamWriter|File\s*\.\s*Create",
              "writes files",
              "Normal for tools that save settings; worth confirming the destination."),
        };

        /// <summary>
        /// Reads the payload of every code entry and applies the rules.
        /// Only code payloads are read, so this is far cheaper than a full pass.
        /// </summary>
        public static List<ScriptReport> Audit(string packagePath, List<UpkgRow> rows,
                                               Func<float, bool> onProgress = null)
        {
            var codeRows = new Dictionary<string, UpkgRow>(StringComparer.Ordinal);
            foreach (var row in rows)
                if (IsCode(row.Entry)) codeRows[row.Entry.Guid] = row;

            var reports = new Dictionary<string, ScriptReport>(StringComparer.Ordinal);
            if (codeRows.Count == 0) return new List<ScriptReport>();

            UpkgArchive.Read(packagePath,
                want: m => m.Name == "asset" && codeRows.ContainsKey(m.Guid),
                onPayload: (m, data) =>
                {
                    var row = codeRows[m.Guid];
                    var report = new ScriptReport { Row = row };
                    reports[m.Guid] = report;

                    if (BinaryCodeExtensions.Contains(row.Entry.Extension))
                    {
                        report.IsBinary = true;
                        return;
                    }

                    report.Source = DecodeText(data);
                    Apply(report);
                },
                onProgress: (pos, total) =>
                {
                    if (onProgress == null) return true;
                    return onProgress(total > 0 ? (float)pos / total : 0f);
                });

            // Code entries with no payload (a folder, or an empty file) still listed.
            foreach (var kv in codeRows)
                if (!reports.ContainsKey(kv.Key))
                    reports[kv.Key] = new ScriptReport { Row = kv.Value, Source = "" };

            var list = new List<ScriptReport>(reports.Values);
            list.Sort((a, b) =>
            {
                int c = b.Worst.CompareTo(a.Worst);
                if (c != 0) return c;
                c = b.Findings.Count.CompareTo(a.Findings.Count);
                if (c != 0) return c;
                return string.Compare(a.Row.Entry.PathName, b.Row.Entry.PathName,
                                      StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static string DecodeText(byte[] data)
        {
            // Strip a BOM if present, then read as UTF-8 with replacement.
            int offset = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                offset = 3;
            return Encoding.UTF8.GetString(data, offset, data.Length - offset);
        }

        static readonly Regex UrlPattern = new Regex(
            @"https?://[^\s""'<>\\)\]]+", RegexOptions.Compiled);

        // Suffixes that are themselves a registry, so the name people recognise is
        // one label further left: "booth.pm" but "someone.co.uk".
        static readonly HashSet<string> MultiLabelSuffixes = new HashSet<string>
        {
            "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "net.uk",
            "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
            "com.au", "net.au", "org.au", "com.br", "com.cn", "com.mx",
            "com.tr", "com.tw", "co.kr", "co.nz", "co.za", "co.in", "com.sg",
            "github.io", "gitlab.io", "pages.dev", "workers.dev", "web.app",
            "firebaseapp.com", "herokuapp.com", "netlify.app", "vercel.app",
            "amazonaws.com", "blob.core.windows.net", "s3.amazonaws.com",
        };

        public static string HostOf(string url)
        {
            int start = url.IndexOf("//", StringComparison.Ordinal);
            if (start < 0) return url;
            start += 2;

            int end = url.Length;
            foreach (var stop in new[] { '/', ':', '?', '#' })
            {
                int at = url.IndexOf(stop, start);
                if (at >= 0 && at < end) end = at;
            }

            var host = url.Substring(start, end - start);
            int credentials = host.LastIndexOf('@');
            if (credentials >= 0) host = host.Substring(credentials + 1);
            return host.Trim('.').ToLowerInvariant();
        }

        /// <summary>
        /// The part of a host a person would recognise, with subdomains dropped:
        /// "cdn.assets.booth.pm" and "booth.pm" are both "booth.pm".
        /// </summary>
        public static string RegistrableDomain(string host)
        {
            if (string.IsNullOrEmpty(host)) return host;

            // A bare IP address has no domain structure to reduce.
            if (Regex.IsMatch(host, @"\A\d{1,3}(\.\d{1,3}){3}\z")) return host;

            var labels = host.Split('.');
            if (labels.Length <= 2) return host;

            var lastTwo = $"{labels[labels.Length - 2]}.{labels[labels.Length - 1]}";
            if (labels.Length >= 3)
            {
                var lastThree = $"{labels[labels.Length - 3]}.{lastTwo}";
                if (MultiLabelSuffixes.Contains(lastThree)) return lastThree;
            }
            if (MultiLabelSuffixes.Contains(lastTwo) && labels.Length >= 3)
                return $"{labels[labels.Length - 3]}.{lastTwo}";

            return lastTwo;
        }

        static readonly Regex StringLiteral = new Regex(
            "\"([^\"\\\\\n]|\\\\.){24,}\"", RegexOptions.Compiled);

        /// <summary>
        /// Shannon entropy per character. Ordinary prose and code identifiers sit
        /// well below 4; base64 blobs and ciphertext sit above it.
        /// </summary>
        static double Entropy(string s)
        {
            var counts = new Dictionary<char, int>();
            foreach (var c in s)
            {
                counts.TryGetValue(c, out int n);
                counts[c] = n + 1;
            }
            double total = s.Length, bits = 0;
            foreach (var kv in counts)
            {
                double p = kv.Value / total;
                bits -= p * Math.Log(p, 2);
            }
            return bits;
        }

        static void CollectDomainsAndObfuscation(ScriptReport report)
        {
            foreach (Match m in UrlPattern.Matches(report.Source))
            {
                var url = m.Value.TrimEnd('.', ',', ';');
                if (url.Length > 0) report.Urls.Add(url);
            }

            foreach (Match m in StringLiteral.Matches(report.Source))
            {
                var body = m.Value.Trim('"');
                if (body.Length < 24) continue;

                // A long literal that is both high-entropy and made only of the
                // characters encodings use is not something anyone typed on purpose.
                bool encodedAlphabet = Regex.IsMatch(body, @"\A[A-Za-z0-9+/=_\-]+\z");
                if (encodedAlphabet && body.Length >= 40 && Entropy(body) > 4.2)
                    report.ObfuscatedStrings++;
            }
        }

        static void Apply(ScriptReport report)
        {
            if (string.IsNullOrEmpty(report.Source)) return;

            CollectDomainsAndObfuscation(report);

            var lines = report.Source.Replace("\r\n", "\n").Split('\n');
            foreach (var rule in Rules)
            {
                if (!rule.Pattern.IsMatch(report.Source)) continue;

                int reported = 0;
                for (int i = 0; i < lines.Length && reported < 5; i++)
                {
                    if (!rule.Pattern.IsMatch(lines[i])) continue;
                    var text = lines[i].Trim();
                    if (text.Length > 200) text = text.Substring(0, 200) + " ...";
                    report.Findings.Add(new Finding
                    {
                        Rule = rule,
                        Line = i + 1,
                        Text = text,
                    });
                    reported++;
                }

                // Matched across lines rather than within one (rare, but possible).
                if (reported == 0)
                    report.Findings.Add(new Finding { Rule = rule, Line = 0, Text = "" });
            }
        }

        public class Summary
        {
            public int TotalCode;
            public int Binaries;
            public int High;
            public int Medium;
            public int AutoRun;

            public bool AnythingSerious { get { return High > 0 || Binaries > 0; } }
        }

        /// <summary>
        /// What the code in a package can actually do, in plain language.
        ///
        /// This is what a normal user should be shown: file names and line numbers
        /// answer "where", but the question being asked is "what would this do to my
        /// machine". The per-file detail stays available for anyone who wants it.
        /// </summary>
        public class Capabilities
        {
            public int TotalCode;                  // for the import plan, not for display
            public int ObfuscatedStrings;

            // When the scripts take control, phrased as moments the user recognises.
            public bool RunsOnImport;              // the instant the package lands
            public bool RunsWhenProjectOpens;      // every editor start, forever
            public bool RunsWhenPlaying;

            /// <summary>
            /// Some of it is in a form nobody can read before running it. The user
            /// never needs the word "DLL" to understand what that costs them.
            /// </summary>
            public bool Unreadable { get { return Binaries.Count > 0; } }

            public readonly List<string> Binaries = new List<string>();
            // Registrable domain -> the full URLs seen under it. Subdomains are
            // rolled up, so "booth.pm" is one entry however many hosts it had.
            public readonly SortedDictionary<string, SortedSet<string>> Domains =
                new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

            public void AddUrl(string url)
            {
                var domain = RegistrableDomain(HostOf(url));
                if (string.IsNullOrEmpty(domain)) return;

                SortedSet<string> urls;
                if (!Domains.TryGetValue(domain, out urls))
                    Domains[domain] = urls =
                        new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                urls.Add(url);
            }
            public readonly SortedSet<string> Hooks =
                new SortedSet<string>(StringComparer.Ordinal);
            public readonly SortedSet<string> Abilities =
                new SortedSet<string>(StringComparer.Ordinal);

            public bool RunsByItself
            {
                get { return RunsOnImport || RunsWhenProjectOpens || RunsWhenPlaying; }
            }

            public bool Quiet
            {
                get
                {
                    return !RunsByItself && Domains.Count == 0 && Abilities.Count == 0 &&
                           ObfuscatedStrings == 0 && !Unreadable;
                }
            }

            /// <summary>When the scripts take control, as one readable sentence.</summary>
            public string WhenItRuns()
            {
                var moments = new List<string>();
                if (RunsOnImport) moments.Add("the moment this package is added");
                if (RunsWhenProjectOpens) moments.Add("every time you open this project");
                if (RunsWhenPlaying) moments.Add("whenever you enter Play mode");
                if (moments.Count == 0) return null;

                var text = moments[0];
                for (int i = 1; i < moments.Count; i++)
                    text += (i == moments.Count - 1 ? ", and " : ", ") + moments[i];
                return $"These scripts start running by themselves {text}. You are not asked first.";
            }
        }

        // Rule id -> what it means for the person deciding, in their words rather
        // than the vocabulary of the thing being detected. No jargon, no counts:
        // one script can do as much harm as a thousand.
        static readonly Dictionary<string, string> AbilityText =
            new Dictionary<string, string>
            {
                { "network",        "Connect to the internet" },
                { "webhook",        "Send information to a chat service like Discord" },
                { "process",        "Start other programs on your computer" },
                { "native",         "Run code that Unity cannot check" },
                { "registry",       "Change Windows settings that stay after you uninstall" },
                { "assembly-load",  "Download and run more code that never appears in your project" },
                { "compile",        "Write and run brand new code while Unity is open" },
                { "delete",         "Delete files" },
                { "write-outside",  "Read and change files outside this project" },
                { "openurl",        "Open web pages in your browser" },
                { "base64",         "Unscramble hidden text" },
            };

        public static Capabilities Describe(List<ScriptReport> reports)
        {
            var caps = new Capabilities();
            foreach (var report in reports)
            {
                caps.TotalCode++;
                if (report.IsBinary)
                {
                    caps.Binaries.Add(report.Row.Entry.PathName);
                    continue;
                }

                caps.ObfuscatedStrings += report.ObfuscatedStrings;
                foreach (var url in report.Urls) caps.AddUrl(url);

                foreach (var finding in report.Findings)
                {
                    var id = finding.Rule.Id;

                    // An InitializeOnLoad script runs on import AND on every editor
                    // start after it - both are worth saying out loud.
                    if (id == "auto-run")
                    {
                        caps.RunsOnImport = true;
                        caps.RunsWhenProjectOpens = true;
                    }
                    if (id == "editor-hook") caps.RunsWhenProjectOpens = true;
                    if (id == "auto-run-runtime") caps.RunsWhenPlaying = true;

                    string ability;
                    if (AbilityText.TryGetValue(id, out ability)) caps.Abilities.Add(ability);

                    if (id == "long-literal" || id == "escaped-string")
                        caps.ObfuscatedStrings++;
                }
            }
            return caps;
        }

        public static Summary Summarize(List<ScriptReport> reports)
        {
            var s = new Summary();
            foreach (var report in reports)
            {
                s.TotalCode++;
                if (report.IsBinary) { s.Binaries++; continue; }
                if (report.Worst == Severity.High) s.High++;
                else if (report.Worst == Severity.Medium) s.Medium++;
                foreach (var f in report.Findings)
                    if (f.Rule.Id == "auto-run") { s.AutoRun++; break; }
            }
            return s;
        }
    }
}
