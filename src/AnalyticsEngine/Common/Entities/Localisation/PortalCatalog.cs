using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Common.Entities.Localisation
{
    /// <summary>The languages the web portal ships in.</summary>
    public enum PortalLanguage
    {
        English,
        Spanish,
    }

    public static class PortalLanguages
    {
        /// <summary>
        /// The language a caller asked for: the portal's own codes (<c>en</c>, <c>es</c>) or a tag starting with one
        /// (<c>es-ES</c>). Anything else - including nothing, which is what a link from before this parameter sends - is
        /// English, so no request ever reaches <see cref="CultureInfo"/> with a culture name it chose.
        /// </summary>
        public static PortalLanguage Parse(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return PortalLanguage.English;
            var primary = code.Trim().Split('-', '_')[0];
            return string.Equals(primary, "es", StringComparison.OrdinalIgnoreCase) ? PortalLanguage.Spanish : PortalLanguage.English;
        }

        /// <summary>The portal's code for a language, as its catalog folders are named.</summary>
        public static string Code(PortalLanguage language) => language == PortalLanguage.Spanish ? "es" : "en";

        /// <summary>The culture the portal formats numbers and dates in for a language (<c>en-GB</c> or <c>es-ES</c>).</summary>
        public static CultureInfo Culture(PortalLanguage language) =>
            CultureInfo.GetCultureInfo(language == PortalLanguage.Spanish ? "es-ES" : "en-GB");
    }

    /// <summary>
    /// One module of the web portal's translation catalog (<c>Web/Scripts/portal/src/i18n/catalog/{en,es}/*.ts</c>), embedded
    /// in this assembly at build time. Text the server writes for a reader - the Copilot investment workbook - comes from the
    /// same catalog as the page, so it is translated, placeholder-checked and English-in-Spanish-checked by the portal's own
    /// gate, and cannot drift into a second, untracked wording.
    /// </summary>
    /// <remarks>
    /// The catalog files are TypeScript, but a strict subset of it: one <c>'key': 'value',</c> entry per item, the value a
    /// single-quoted literal that may sit on the next line. <see cref="Parse"/> accepts exactly that and throws on anything
    /// else, so a change of format fails the tests instead of silently dropping text.
    /// </remarks>
    public sealed class PortalCatalog
    {
        private static readonly ConcurrentDictionary<string, Lazy<PortalCatalog>> Loaded = new ConcurrentDictionary<string, Lazy<PortalCatalog>>(StringComparer.Ordinal);

        private static readonly Regex Placeholder = new Regex(@"\{(\w+)\}", RegexOptions.CultureInvariant);

        private readonly IReadOnlyDictionary<string, string> _entries;
        private readonly PortalCatalog _fallback;

        private PortalCatalog(PortalLanguage language, IReadOnlyDictionary<string, string> entries, PortalCatalog fallback)
        {
            Language = language;
            _entries = entries;
            _fallback = fallback;
        }

        public PortalLanguage Language { get; }

        public CultureInfo Culture => PortalLanguages.Culture(Language);

        /// <summary>Every entry of this language's module, as written in the catalog file.</summary>
        public IReadOnlyDictionary<string, string> Entries => _entries;

        /// <summary>The embedded resource a module's catalog is read from.</summary>
        public static string ResourceName(string module, PortalLanguage language) =>
            "Common.Entities.PortalCatalog." + PortalLanguages.Code(language) + "." + module + ".ts";

        /// <summary>
        /// A catalog module in a language, read once per process. A key Spanish lacks reads in English, as the portal
        /// does; the portal's build makes that impossible for a key it ships.
        /// </summary>
        public static PortalCatalog For(string module, PortalLanguage language)
        {
            if (string.IsNullOrWhiteSpace(module)) throw new ArgumentException("A catalog module is required.", nameof(module));
            return Loaded.GetOrAdd(module + "|" + PortalLanguages.Code(language), _ => new Lazy<PortalCatalog>(() =>
            {
                var fallback = language == PortalLanguage.English ? null : For(module, PortalLanguage.English);
                return new PortalCatalog(language, Parse(ReadResource(ResourceName(module, language))), fallback);
            })).Value;
        }

        public bool Has(string key) => _entries.ContainsKey(key) || (_fallback != null && _fallback.Has(key));

        /// <summary>The text for a key, with each <c>{name}</c> replaced by its value. A key no language has is a defect.</summary>
        public string Text(string key, params (string Name, string Value)[] values)
        {
            if (!_entries.TryGetValue(key, out var template))
            {
                if (_fallback == null) throw new KeyNotFoundException("The portal catalog has no text for '" + key + "'.");
                return _fallback.Text(key, values);
            }
            if (values == null || values.Length == 0) return template;
            return Placeholder.Replace(template, match =>
            {
                foreach (var value in values)
                    if (string.Equals(value.Name, match.Groups[1].Value, StringComparison.Ordinal)) return value.Value ?? string.Empty;
                return match.Value;
            });
        }

        /// <summary>The names of the placeholders a text takes, for checks that every one is supplied.</summary>
        public static IReadOnlyCollection<string> PlaceholdersOf(string text)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match match in Placeholder.Matches(text ?? string.Empty)) names.Add(match.Groups[1].Value);
            return names;
        }

        private static string ReadResource(string name)
        {
            using (var stream = typeof(PortalCatalog).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidOperationException("The portal catalog '" + name + "' is not embedded in Common.Entities.");
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    return reader.ReadToEnd();
            }
        }

        private static readonly Regex ObjectStart = new Regex(@"^(export\s+)?const \w+(\s*:[^=]+)?\s*=\s*\{\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex ObjectEnd = new Regex(@"^\}(\s+as\s+const)?\s*;\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex KeyLine = new Regex(@"^'(?<key>[A-Za-z0-9_.\-]+)':\s*(?<rest>.*)$", RegexOptions.CultureInvariant);

        /// <summary>Reads the entries of one catalog file. Throws <see cref="FormatException"/> on anything it does not recognise.</summary>
        public static Dictionary<string, string> Parse(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            var inside = false;
            var closed = false;
            var inComment = false;
            string pendingKey = null;
            var pendingLine = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (!inside)
                {
                    if (ObjectStart.IsMatch(line)) inside = true;
                    continue;
                }
                if (inComment)
                {
                    if (line.Contains("*/")) inComment = false;
                    continue;
                }
                if (pendingKey == null)
                {
                    if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("/*", StringComparison.Ordinal))
                    {
                        inComment = !line.Contains("*/");
                        continue;
                    }
                    if (ObjectEnd.IsMatch(line))
                    {
                        closed = true;
                        break;
                    }
                    var match = KeyLine.Match(line);
                    if (!match.Success) throw Unrecognised(index, lines[index]);
                    var key = match.Groups["key"].Value;
                    var rest = match.Groups["rest"].Value;
                    if (rest.Length == 0)
                    {
                        pendingKey = key;
                        pendingLine = index;
                        continue;
                    }
                    Add(entries, key, ReadLiteral(rest, index, lines[index]), index);
                }
                else
                {
                    if (line.Length == 0) throw Unrecognised(pendingLine, lines[pendingLine]);
                    Add(entries, pendingKey, ReadLiteral(line, index, lines[index]), index);
                    pendingKey = null;
                }
            }
            if (!inside || !closed || pendingKey != null) throw new FormatException("The catalog file has no complete 'const ... = { ... }' object.");
            return entries;
        }

        private static void Add(Dictionary<string, string> entries, string key, string value, int index)
        {
            if (entries.ContainsKey(key)) throw new FormatException(string.Format(CultureInfo.InvariantCulture, "Line {0}: '{1}' appears twice.", index + 1, key));
            entries.Add(key, value);
        }

        /// <summary>Reads one single-quoted string literal and checks nothing but a comma or a comment follows it.</summary>
        private static string ReadLiteral(string text, int index, string original)
        {
            if (text.Length == 0 || text[0] != '\'') throw Unrecognised(index, original);
            var value = new StringBuilder();
            var position = 1;
            for (; position < text.Length; position++)
            {
                var c = text[position];
                if (c == '\'') break;
                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }
                if (++position >= text.Length) throw Unrecognised(index, original);
                switch (text[position])
                {
                    case '\\': value.Append('\\'); break;
                    case '\'': value.Append('\''); break;
                    case '"': value.Append('"'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u':
                        if (position + 1 < text.Length && text[position + 1] == '{')
                        {
                            var close = text.IndexOf('}', position + 2);
                            if (close < 0) throw Unrecognised(index, original);
                            value.Append(char.ConvertFromUtf32(int.Parse(text.Substring(position + 2, close - position - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                            position = close;
                        }
                        else
                        {
                            if (position + 4 >= text.Length) throw Unrecognised(index, original);
                            value.Append((char)int.Parse(text.Substring(position + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            position += 4;
                        }
                        break;
                    default:
                        throw Unrecognised(index, original);
                }
            }
            if (position >= text.Length) throw Unrecognised(index, original);
            var tail = text.Substring(position + 1).Trim();
            if (tail.StartsWith(",", StringComparison.Ordinal)) tail = tail.Substring(1).Trim();
            if (tail.Length > 0 && !tail.StartsWith("//", StringComparison.Ordinal)) throw Unrecognised(index, original);
            return value.ToString();
        }

        private static FormatException Unrecognised(int index, string line) =>
            new FormatException(string.Format(CultureInfo.InvariantCulture, "Line {0} of the portal catalog is not a 'key': 'value' entry: {1}", index + 1, line.Trim()));
    }
}
