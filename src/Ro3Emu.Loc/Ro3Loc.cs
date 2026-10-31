using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro3Emu.Loc
{
    /// <summary>
    /// Chinese -> English at the point text is drawn. Cecil puts a call to <see cref="Map"/>
    /// in front of TMP_Text.text / SetText, TextMeshPro(UGUI).Awake and HUDTextHelper.SetWordCell, so baked prefab text,
    /// Lua literals, raw config strings and C# strings all pass through here.
    /// <list type="bullet">
    /// <item>fast path: a string without a CJK character is returned as is;</item>
    /// <item>exact map, then a normalized retry (trim, zero-width spaces, \r\n);</item>
    /// <item>rich text and multi-line text: each tag-free / line segment is mapped on its own;</item>
    /// <item>templates: keys with %d / %s / {0} / ${1} / @{1} match runtime-built strings;</item>
    /// <item>misses are appended once to &lt;client&gt;/ro3emu/loc_misses.tsv (collect mode).</item>
    /// </list>
    /// Data: the embedded loc_en.tsv, then &lt;client&gt;/ro3emu/loc_en.tsv (overrides). Lines are "zh\ten" with \t \n \\ escaped.
    /// While language.txt selects Spanish, &lt;client&gt;/ro3emu/loc_sid_es.tsv ("sid\ttext") replaces texts the client looks
    /// up by id (<see cref="Sid"/>).
    /// </summary>
    public static class Ro3Loc
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string> _map;
        private static List<Template> _templates;
        private static Dictionary<long, string> _sids;
        private static Dictionary<long, string> _texts;
        private static readonly Dictionary<string, string> Derived = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Misses = new HashSet<string>(StringComparer.Ordinal);
        private static string _dir;
        private static bool _failed;
        private const int MaxCached = 50000;

        /// <summary>Hook 0 = TMP setter, 1 = TMP Awake, 2 = HUD; only used to tag misses.</summary>
        public static string Map(string text) => MapFrom(text, 0);

        public static string MapAwake(string text) => MapFrom(text, 1);

        public static string MapHud(string text) => MapFrom(text, 2);

        /// <summary>
        /// LanguageMain.Get(sid) result. In Spanish, a sid in &lt;client&gt;/ro3emu/loc_sid_es.tsv (our translation of the
        /// sid's Chinese text, which the client would show in its own English) returns that text. A sid that is in no
        /// Localization table comes back "" / "None" / "Not Find Key", so the label would be empty. Those sids map to the
        /// Chinese baked into the prefab (loc_sids.tsv), then to English.
        /// </summary>
        public static string Sid(string value, long sid)
        {
            try
            {
                lock (Gate)
                {
                    if (_map == null) Load();
                    if (_texts != null && _texts.TryGetValue(sid, out var text)) return text;
                }
            }
            catch
            {
                // keep the value the client resolved
            }
            if (string.IsNullOrEmpty(value) || value == "None" || value.StartsWith("Not Find Key", StringComparison.Ordinal))
            {
                try
                {
                    string zh = null;
                    lock (Gate)
                    {
                        if (_map == null) Load();
                        if (_sids != null) _sids.TryGetValue(sid, out zh);
                    }
                    if (zh != null) return MapFrom(zh, 0);
                }
                catch
                {
                    // keep the value the client resolved
                }
                return value;
            }
            return MapFrom(value, 0);
        }

        /// <summary>English the client ships that we replace (en_overrides.tsv): exact strings, checked before the CJK fast path.</summary>
        private static readonly Dictionary<string, string> EnglishOverrides = LoadEnglishOverrides();

        private static string ResolveRo3EmuDir()
        {
            try
            {
                string asmLocation = typeof(Ro3Loc).Assembly.Location;
                if (!string.IsNullOrEmpty(asmLocation))
                {
                    string candidate = Path.Combine(Path.GetDirectoryName(asmLocation) ?? "", "..", "..", "ro3emu");
                    if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            catch { }
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
                string candidate = Path.Combine(baseDir, "ro3emu");
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
            try
            {
                string curDir = Directory.GetCurrentDirectory();
                string candidate = Path.Combine(curDir, "ro3emu");
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "ro3emu");
        }

        private static string GetConfiguredLanguage(string dir)
        {
            try
            {
                string langFile = Path.Combine(dir, "language.txt");
                if (!File.Exists(langFile) && Directory.Exists(Path.Combine(dir, ".."))) { string parentLang = Path.Combine(dir, "..", "language.txt"); if (File.Exists(parentLang)) langFile = parentLang; }
                if (File.Exists(langFile))
                {
                    string text = File.ReadAllText(langFile).Trim().ToLowerInvariant();
                    if (text.StartsWith("en")) return "en";
                    if (text.StartsWith("pt")) return "pt-br";
                    if (text.StartsWith("es")) return "es";
                }
            }
            catch { }
            return "es";
        }

        private static Dictionary<string, string> LoadEnglishOverrides()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string dir = ResolveRo3EmuDir();
            string lang = GetConfiguredLanguage(dir);

            if (lang == "en")
            {
                try
                {
                    string enFile = Path.Combine(dir, "en_overrides_en.tsv");
                    if (File.Exists(enFile))
                    {
                        using (var reader = new StreamReader(enFile, Encoding.UTF8))
                            ReadOverrides(reader, map);
                    }
                }
                catch { }
                return map;
            }
            try
            {
                using (var stream = typeof(Ro3Loc).Assembly.GetManifestResourceStream("en_overrides.tsv"))
                using (var reader = stream == null ? null : new StreamReader(stream, Encoding.UTF8))
                {
                    ReadOverrides(reader, map);
                }
            }
            catch
            {
                // no embedded overrides
            }
            try
            {
                string file = Path.Combine(dir, "en_overrides.tsv");
                if (File.Exists(file))
                {
                    using (var reader = new StreamReader(file, Encoding.UTF8))
                    {
                        ReadOverrides(reader, map);
                    }
                }
            }
            catch
            {
                // no disk overrides
            }
            return map;
        }

        private static string StripBom(string s)
        {
            if (s != null && s.Length > 0 && (s[0] == '\uFEFF' || s[0] == '\uFFFE'))
                return s.Substring(1);
            return s;
        }

        private static void ReadOverrides(TextReader reader, Dictionary<string, string> map)
        {
            if (reader == null) return;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                int tab = line.IndexOf('\t');
                if (line.Length == 0 || line[0] == '#' || tab <= 0) continue;
                map[StripBom(Unescape(line.Substring(0, tab)))] = Unescape(line.Substring(tab + 1));
            }
        }

        private static int CountLeadingSpaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int count = 0;
            while (count < s.Length && char.IsWhiteSpace(s[count])) count++;
            return count;
        }

        private static string TryTranslateTaggedOrPrefixed(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // Pattern 1: <color=...>[Prefix]</color> Remainder
            // or <color=...>Prefix</color> Remainder
            if (text.Length > 0 && text[0] == '<')
            {
                int closeTag1 = text.IndexOf('>');
                if (closeTag1 > 0 && closeTag1 + 1 < text.Length)
                {
                    int nextOpen = text.IndexOf('<', closeTag1 + 1);
                    if (nextOpen > closeTag1)
                    {
                        int closeTag2 = text.IndexOf('>', nextOpen + 1);
                        if (closeTag2 > nextOpen)
                        {
                            string openTag = text.Substring(0, closeTag1 + 1);
                            string innerTag = text.Substring(closeTag1 + 1, nextOpen - (closeTag1 + 1));
                            string endTag = text.Substring(nextOpen, closeTag2 - nextOpen + 1);
                            string rest = text.Substring(closeTag2 + 1);

                            string innerTrimmed = innerTag.Trim();
                            string translatedInner = null;

                            if (EnglishOverrides.TryGetValue(innerTag, out var ti)) translatedInner = ti;
                            else if (EnglishOverrides.TryGetValue(innerTrimmed, out ti)) translatedInner = ti;
                            else if (HasCjk(innerTrimmed))
                            {
                                lock (Gate)
                                {
                                    if (_map == null) Load();
                                    if (_map != null && _map.TryGetValue(innerTrimmed, out var zhInner))
                                        translatedInner = zhInner;
                                }
                            }

                            if (translatedInner != null)
                            {
                                string translatedRest = rest;
                                string restTrimmed = rest.Trim();
                                if (!string.IsNullOrEmpty(restTrimmed))
                                {
                                    if (EnglishOverrides.TryGetValue(restTrimmed, out var tr))
                                    {
                                        int spaces = CountLeadingSpaces(rest);
                                        string prefix = spaces > 0 ? rest.Substring(0, spaces) : " ";
                                        translatedRest = prefix + tr;
                                    }
                                    else if (HasCjk(restTrimmed))
                                    {
                                        lock (Gate)
                                        {
                                            if (_map == null) Load();
                                            if (_map != null && _map.TryGetValue(restTrimmed, out var zhRest))
                                            {
                                                int spaces = CountLeadingSpaces(rest);
                                                string prefix = spaces > 0 ? rest.Substring(0, spaces) : " ";
                                                translatedRest = prefix + zhRest;
                                            }
                                        }
                                    }
                                }
                                return openTag + translatedInner + endTag + translatedRest;
                            }
                        }
                    }
                }
            }

            // Pattern 2: [Prefix] Remainder
            if (text.Length > 0 && text[0] == '[')
            {
                foreach (var kvp in EnglishOverrides)
                {
                    if (kvp.Key.Length > 1 && kvp.Key[0] == '[' && text.StartsWith(kvp.Key, StringComparison.Ordinal))
                    {
                        string remainder = text.Substring(kvp.Key.Length);
                        string trimmed = remainder.Trim();
                        if (trimmed.Length > 0)
                        {
                            if (EnglishOverrides.TryGetValue(trimmed, out var remainderTrans))
                            {
                                int spaces = CountLeadingSpaces(remainder);
                                string prefix = spaces > 0 ? remainder.Substring(0, spaces) : " ";
                                return kvp.Value + prefix + remainderTrans;
                            }
                            if (HasCjk(trimmed))
                            {
                                lock (Gate)
                                {
                                    if (_map == null) Load();
                                    if (_map != null && _map.TryGetValue(trimmed, out var zhRest))
                                    {
                                        int spaces = CountLeadingSpaces(remainder);
                                        string prefix = spaces > 0 ? remainder.Substring(0, spaces) : " ";
                                        return kvp.Value + prefix + zhRest;
                                    }
                                }
                            }
                        }
                        return kvp.Value + remainder;
                    }
                }
            }

            return null;
        }

        private static string MapFrom(string text, int hook)
        {
            if (text == null || text.Length == 0) return text;
            if (EnglishOverrides.Count != 0)
            {
                if (EnglishOverrides.TryGetValue(text, out var replaced)) return replaced;

                string trimmed = text.Trim();
                if (trimmed.Length != text.Length && EnglishOverrides.TryGetValue(trimmed, out replaced))
                {
                    int leading = CountLeadingSpaces(text);
                    string prefix = leading > 0 ? text.Substring(0, leading) : "";
                    int trailing = text.Length - trimmed.Length - leading;
                    string suffix = trailing > 0 ? text.Substring(text.Length - trailing) : "";
                    return prefix + replaced + suffix;
                }

                string tagged = TryTranslateTaggedOrPrefixed(text);
                if (tagged != null) return tagged;

                if (text.IndexOf('<') >= 0 || text.IndexOf('\n') >= 0)
                {
                    string segmented = Segments(text);
                    if (segmented != null) return segmented;
                }
            }
            if (!HasCjk(text)) return text;
            try
            {
                lock (Gate)
                {
                    if (_map == null) Load();
                    if (_failed) return text;
                    if (_map.TryGetValue(text, out var en)) return en;
                    if (Derived.TryGetValue(text, out en)) return en ?? text;
                    if (Misses.Contains(text)) return text;
                    en = Resolve(text);
                    if (en != null)
                    {
                        if (Derived.Count < MaxCached) Derived[text] = en;
                        return en;
                    }
                    if (Misses.Count < MaxCached && Misses.Add(text)) LogMiss(text, hook);
                    return text;
                }
            }
            catch
            {
                return text;
            }
        }

        private static bool HasCjk(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (IsCjk(s[i])) return true;
            return false;
        }

        // Han and kana only: full-width punctuation alone ("World\uFF1A World") needs no translation.
        private static bool IsCjk(char c) =>
            (c >= '\u3040' && c <= '\u9FFF') || (c >= '\u2E80' && c <= '\u2FDF') || (c >= '\uF900' && c <= '\uFAFF');

        /// <summary>Normalized form, segments, then templates, then CJK runs; null when nothing covers every CJK part.</summary>
        private static string Resolve(string text)
        {
            string norm = Normalize(text);
            if (!ReferenceEquals(norm, text) && _map.TryGetValue(norm, out var en)) return en;
            if (text.IndexOf('<') >= 0 || text.IndexOf('\n') >= 0) return Segments(text);
            return MatchTemplate(text) ?? CjkRuns(text);
        }

        /// <summary>
        /// Text the client composed from an English format and a Chinese name ("利刃重击 is on cooldown.", a skill name
        /// from a table the client reads in Chinese): each maximal CJK run (a middle dot may join two) is mapped on its own.
        /// All or nothing: one unknown run leaves the whole string as it was.
        /// </summary>
        private static string CjkRuns(string text)
        {
            StringBuilder sb = null;
            int i = 0, copied = 0;
            while (i < text.Length)
            {
                if (!IsCjk(text[i])) { i++; continue; }
                int end = i + 1;
                while (end < text.Length && (IsCjk(text[end]) || (text[end] == '·' || text[end] == '・') && end + 1 < text.Length && IsCjk(text[end + 1]))) end++;
                if (i == 0 && end == text.Length) return null; // the whole string: the exact lookup already missed it
                if (!_map.TryGetValue(text.Substring(i, end - i), out var en) || HasCjk(en)) return null;
                if (sb == null) sb = new StringBuilder(text.Length * 2);
                sb.Append(text, copied, i - copied).Append(en);
                copied = i = end;
            }
            if (sb == null) return null;
            return sb.Append(text, copied, text.Length - copied).ToString();
        }

        private static string Normalize(string s)
        {
            string t = s.Trim().Replace("\u200B", "").Replace("\r\n", "\n");
            return t == s ? s : t;
        }

        /// <summary>Maps each text run between rich-text tags and newlines; the tags and separators stay.</summary>
        private static string Segments(string text)
        {
            var sb = new StringBuilder(text.Length * 2);
            int i = 0;
            bool any = false;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '<')
                {
                    int close = text.IndexOf('>', i);
                    if (close < 0) { sb.Append(text, i, text.Length - i); break; }
                    sb.Append(text, i, close - i + 1);
                    i = close + 1;
                    continue;
                }
                if (c == '\n' || c == '\r') { sb.Append(c); i++; continue; }
                int end = i;
                while (end < text.Length && text[end] != '<' && text[end] != '\n' && text[end] != '\r') end++;
                string run = text.Substring(i, end - i);
                if (HasCjk(run))
                {
                    string core = run.Trim();
                    string en = null;
                    if (_map != null && !_map.TryGetValue(core, out en)) en = MatchTemplate(core) ?? CjkRuns(core);
                    if (en != null)
                    {
                        int lead = run.IndexOf(core, StringComparison.Ordinal);
                        sb.Append(run, 0, lead).Append(en).Append(run, lead + core.Length, run.Length - lead - core.Length);
                        any = true;
                    }
                    else
                    {
                        sb.Append(run);
                    }
                }
                else if (EnglishOverrides.Count != 0)
                {
                    string core = run.Trim();
                    if (core.Length > 0 && EnglishOverrides.TryGetValue(core, out var enOverride))
                    {
                        int lead = run.IndexOf(core, StringComparison.Ordinal);
                        sb.Append(run, 0, lead).Append(enOverride).Append(run, lead + core.Length, run.Length - lead - core.Length);
                        any = true;
                    }
                    else
                    {
                        sb.Append(run);
                    }
                }
                else sb.Append(run);
                i = end;
            }
            return any ? sb.ToString() : null;
        }

        private static string MatchTemplate(string text)
        {
            foreach (var t in _templates)
            {
                var m = t.Pattern.Match(text);
                if (!m.Success) continue;
                string en = t.Apply(m);
                if (en != text && !HasCjk(en)) return en;
            }
            return null;
        }

        private static void Load()
        {
            _map = new Dictionary<string, string>(StringComparer.Ordinal);
            _templates = new List<Template>();
            try
            {
                _dir = ResolveRo3EmuDir();
                string lang = GetConfiguredLanguage(_dir);
                if (lang != "en")
                {
                    using (var stream = typeof(Ro3Loc).Assembly.GetManifestResourceStream("loc_en.tsv"))
                        if (stream != null) Read(new StreamReader(stream, Encoding.UTF8));
                }
                _sids = new Dictionary<long, string>();
                using (var stream = typeof(Ro3Loc).Assembly.GetManifestResourceStream("loc_sids.tsv"))
                    if (stream != null) ReadSids(new StreamReader(stream, Encoding.UTF8), _sids);

                string overrides = Path.Combine(_dir, "loc_en.tsv");
                if (File.Exists(overrides)) Read(new StreamReader(overrides, Encoding.UTF8));
            }
            catch
            {
                _failed = _map.Count == 0;
            }
            LoadTexts();
        }

        /// <summary>
        /// &lt;client&gt;/ro3emu/loc_sid_es.tsv ("sid\ttext", written by RO3Emu.exe): Spanish text that replaces a sid's own.
        /// Read only while language.txt selects Spanish; in English the client's own text by id stays.
        /// </summary>
        private static void LoadTexts()
        {
            var texts = new Dictionary<long, string>();
            try
            {
                string dir = _dir ?? ResolveRo3EmuDir();
                string file = Path.Combine(dir, "loc_sid_es.tsv");
                if (GetConfiguredLanguage(dir) == "es" && File.Exists(file)) ReadSids(new StreamReader(file, Encoding.UTF8), texts);
            }
            catch
            {
                // a partly read file still serves what it read
            }
            _texts = texts;
        }

        private static void Read(TextReader reader)
        {
            using (reader)
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string zh = StripBom(Unescape(line.Substring(0, tab)));
                    string en = Unescape(line.Substring(tab + 1));
                    if (en.Length == 0) continue;
                    _map[zh] = en;
                    var template = Template.TryCreate(zh, en);
                    if (template != null) _templates.Add(template);
                }
            }
        }

        private static void ReadSids(TextReader reader, Dictionary<long, string> into)
        {
            using (reader)
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || line[0] == '#' || !long.TryParse(line.Substring(0, tab), out long sid)) continue;
                    into[sid] = Unescape(line.Substring(tab + 1));
                }
            }
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 't' ? '\t' : n == 'n' ? '\n' : n == 'r' ? '\r' : n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void LogMiss(string text, int hook)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string escaped = text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
                File.AppendAllText(Path.Combine(_dir, "loc_misses.tsv"), escaped + "\t" + (hook == 0 ? "set" : hook == 1 ? "awake" : "hud") + "\n", Encoding.UTF8);
            }
            catch
            {
                // collect mode is best effort
            }
        }

        /// <summary>A key with placeholders, compiled to an anchored regex; the English side reuses the captures.</summary>
        private sealed class Template
        {
            private static readonly Regex Token = new Regex(@"%(?:\d+\$)?[-+ 0#]*\d*(?:\.\d+)?[sdfiuxX]|\{\d+\}|[$@]\{\d+\}", RegexOptions.CultureInvariant);
            public Regex Pattern;
            private string[] _zhTokens;
            private string _en;

            public static Template TryCreate(string zh, string en)
            {
                var tokens = Token.Matches(zh);
                // Only keys with Chinese around the tokens: "{0}" alone would match every string.
                if (tokens.Count == 0 || !HasCjk(Token.Replace(zh, ""))) return null;
                var pattern = new StringBuilder("^");
                var names = new string[tokens.Count];
                int last = 0;
                for (int i = 0; i < tokens.Count; i++)
                {
                    pattern.Append(Regex.Escape(zh.Substring(last, tokens[i].Index - last)));
                    pattern.Append("(.+?)");
                    names[i] = tokens[i].Value;
                    last = tokens[i].Index + tokens[i].Length;
                }
                pattern.Append(Regex.Escape(zh.Substring(last))).Append('$');
                return new Template { Pattern = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), _zhTokens = names, _en = en };
            }

            public string Apply(Match m)
            {
                int nextPercent = 0;
                return Token.Replace(_en, t =>
                {
                    int index = -1;
                    if (t.Value[0] == '%')
                    {
                        // printf-style: in order among the % tokens of the key
                        int seen = 0;
                        for (int i = 0; i < _zhTokens.Length; i++)
                            if (_zhTokens[i][0] == '%' && seen++ == nextPercent) { index = i; break; }
                        nextPercent++;
                    }
                    else index = Array.IndexOf(_zhTokens, t.Value);
                    return index >= 0 && index + 1 < m.Groups.Count ? m.Groups[index + 1].Value : t.Value;
                });
            }
        }
    }
}
