#nullable disable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro3Emu.Loc
{
    /// <summary>
    /// English -> Spanish sentences for text the client builds outside the hooked lookup by id: the chat code fills a
    /// Localization_en text (an announcement's ChatConfig content id) and hands the finished English to TextMeshPro.
    /// An English text without placeholders is an exact sentence; one with ${n} / @{n} (params) or ^{n} (ChatConfig
    /// _iContent[n], rich-text colour tags such as &lt;color=#CC762A&gt;) is compiled once to an anchored regex, and the
    /// Spanish text is filled with the same captures. Each template is indexed by one literal word of its English (its
    /// anchor), so a text is only tried against the templates sharing one of its words. Ambiguous English (two different
    /// Spanish texts) is dropped rather than guessed.
    /// Client-free (only mscorlib/System) so tests compile it as is.
    /// </summary>
    public sealed class LocTemplates
    {
        /// <summary>Upper bound on stored sentences: the file is ours, but a broken one must not eat the client's memory.</summary>
        public const int MaxEntries = 60000;

        private const int MinAnchorLength = 3;
        private static readonly Regex Token = new Regex(@"[$@^]\{\d+\}", RegexOptions.CultureInvariant);
        private static readonly char[] Spaces = { ' ', '\t', '\n', '\r' };

        private readonly Dictionary<string, string> _exact = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _templates = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Entry>> _byAnchor = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private readonly HashSet<string> _ambiguous = new HashSet<string>(StringComparer.Ordinal);

        public int Count => _exact.Count + _templates.Count;

        /// <summary>
        /// Adds one sentence. False when it is not used: the Spanish lacks or adds a placeholder, the template has no literal
        /// word to anchor it (it would match nearly anything), or the English was already added with another Spanish.
        /// </summary>
        public bool Add(string en, string es)
        {
            if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(es) || Count >= MaxEntries) return false;
            en = en.Trim();
            if (en.Length == 0 || _ambiguous.Contains(en)) return false;
            var tokens = Token.Matches(en);
            if (!SameTokens(tokens, Token.Matches(es))) return false;
            if (tokens.Count == 0) return AddExact(en, es);
            if (_templates.TryGetValue(en, out var known)) return Conflict(en, known.Es == es);
            var entry = Entry.TryCreate(en, es, tokens);
            if (entry == null) return false;
            _templates[en] = entry;
            if (!_byAnchor.TryGetValue(entry.Anchor, out var list)) _byAnchor[entry.Anchor] = list = new List<Entry>();
            list.Add(entry);
            return true;
        }

        /// <summary>
        /// The Spanish sentence for <paramref name="text"/> (surrounding white space kept), or null. A ${n} / @{n} capture
        /// goes through <paramref name="mapCapture"/> (null result: kept as it is).
        /// </summary>
        public string Translate(string text, Func<string, string> mapCapture)
        {
            if (string.IsNullOrEmpty(text) || Count == 0) return null;
            string core = text.Trim();
            if (core.Length == 0) return null;
            string es = null;
            if (!_exact.TryGetValue(core, out es) && _byAnchor.Count != 0) es = Fill(core, mapCapture);
            if (es == null) return null;
            int lead = text.IndexOf(core, StringComparison.Ordinal);
            int tail = lead + core.Length;
            return new StringBuilder(text.Length + es.Length).Append(text, 0, lead).Append(es).Append(text, tail, text.Length - tail).ToString();
        }

        private bool AddExact(string en, string es)
        {
            if (_exact.TryGetValue(en, out var known)) return Conflict(en, known == es);
            _exact[en] = es;
            return true;
        }

        private bool Conflict(string en, bool same)
        {
            if (same) return true;
            _ambiguous.Add(en);
            if (_exact.Remove(en)) return false;
            if (_templates.TryGetValue(en, out var entry))
            {
                _templates.Remove(en);
                _byAnchor[entry.Anchor].Remove(entry);
            }
            return false;
        }

        private string Fill(string core, Func<string, string> mapCapture)
        {
            HashSet<Entry> tried = null;
            foreach (string word in core.Split(Spaces, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!_byAnchor.TryGetValue(word, out var list)) continue;
                foreach (var entry in list)
                {
                    if (tried == null) tried = new HashSet<Entry>();
                    if (!tried.Add(entry)) continue;
                    var m = entry.Pattern.Match(core);
                    if (m.Success) return entry.Apply(m, mapCapture);
                }
            }
            return null;
        }

        private static bool SameTokens(MatchCollection a, MatchCollection b)
        {
            var left = new HashSet<string>(StringComparer.Ordinal);
            var right = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in a) left.Add(m.Value);
            foreach (Match m in b) right.Add(m.Value);
            return left.SetEquals(right);
        }

        private sealed class Entry
        {
            public Regex Pattern;
            public string Es;
            public string Anchor;
            private string[] _groupTokens;

            public static Entry TryCreate(string en, string es, MatchCollection tokens)
            {
                var pattern = new StringBuilder("^");
                var groups = new string[tokens.Count];
                string anchor = "";
                int last = 0;
                for (int i = 0; i <= tokens.Count; i++)
                {
                    int end = i < tokens.Count ? tokens[i].Index : en.Length;
                    string literal = en.Substring(last, end - last);
                    pattern.Append(Regex.Escape(literal));
                    anchor = Longer(anchor, AnchorOf(literal, last > 0, i < tokens.Count));
                    if (i == tokens.Count) break;
                    groups[i] = tokens[i].Value;
                    // ^{n}: the colour tags ChatConfig puts there (none or several); ${n} / @{n}: any text.
                    pattern.Append(tokens[i].Value[0] == '^' ? "((?:<[^>]*>)*)" : "(.+?)");
                    last = tokens[i].Index + tokens[i].Length;
                }
                if (anchor.Length < MinAnchorLength) return null;
                pattern.Append('$');
                return new Entry
                {
                    Pattern = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant),
                    Es = es,
                    Anchor = anchor,
                    _groupTokens = groups,
                };
            }

            /// <summary>
            /// The longest whole word of a literal part: a fragment touching a placeholder (".", "Lv.") is glued to the
            /// filled text in the finished sentence, so it is no word of that sentence.
            /// </summary>
            private static string AnchorOf(string literal, bool afterToken, bool beforeToken)
            {
                string[] words = literal.Split(Spaces);
                string best = "";
                for (int w = 0; w < words.Length; w++)
                {
                    if (w == 0 && afterToken) continue;
                    if (w == words.Length - 1 && beforeToken) continue;
                    best = Longer(best, words[w]);
                }
                return best;
            }

            private static string Longer(string a, string b) => b.Length > a.Length ? b : a;

            public string Apply(Match m, Func<string, string> mapCapture)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < _groupTokens.Length; i++)
                {
                    if (values.ContainsKey(_groupTokens[i])) continue;
                    string value = m.Groups[i + 1].Value;
                    if (_groupTokens[i][0] != '^' && mapCapture != null) value = mapCapture(value) ?? value;
                    values[_groupTokens[i]] = value;
                }
                return Token.Replace(Es, t => values.TryGetValue(t.Value, out var v) ? v : t.Value);
            }
        }
    }
}
