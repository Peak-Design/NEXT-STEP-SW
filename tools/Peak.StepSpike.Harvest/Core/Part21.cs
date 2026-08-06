using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Peak.NextStep.Core
{
    /// <summary>
    /// Minimal ISO 10303-21 reader/rewriter.
    ///
    /// Deliberately text-level and additive: the exporter's whole value is that
    /// SolidWorks' geometry section is left byte-identical (no OCCT
    /// re-tolerancing, no lost PMI, and SolidWorks' own per-face styling --
    /// which S0 proved is correct -- survives exactly as written). We only
    /// append presentation entities and, where required, rewrite the styled
    /// items that are wrong.
    /// </summary>
    public sealed class Part21
    {
        private static readonly Regex EntityRe =
            new Regex(@"^#(\d+)\s*=\s*([A-Z0-9_]+)?\s*\(", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex RefRe = new Regex(@"#(\d+)", RegexOptions.Compiled);

        public string Text { get; private set; }
        public string Path { get; }

        /// <summary>id -> (type, argument text including the outer parens)</summary>
        public Dictionary<int, KeyValuePair<string, string>> Entities { get; } =
            new Dictionary<int, KeyValuePair<string, string>>();

        private int _nextId;

        public Part21(string path)
        {
            Path = path;
            Text = File.ReadAllText(path);
            Parse();
        }

        private void Parse()
        {
            int dataStart = Text.IndexOf("DATA;", StringComparison.Ordinal);
            if (dataStart < 0) throw new InvalidDataException("no DATA section");

            foreach (var stmt in SplitStatements(Text.Substring(dataStart + 5)))
            {
                var s = stmt.Trim();
                if (s.Length == 0 || s[0] != '#') continue;
                var m = EntityRe.Match(s);
                if (!m.Success) continue;
                int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string type = (m.Groups[2].Value ?? "").ToUpperInvariant();
                if (type.Length == 0)
                {
                    var inner = Regex.Match(s, @"\(\s*([A-Z0-9_]+)\s*\(");
                    type = "COMPLEX:" + (inner.Success ? inner.Groups[1].Value : "?");
                }
                Entities[id] = new KeyValuePair<string, string>(type, s.Substring(m.Length - 1));
                if (id >= _nextId) _nextId = id + 1;
            }
        }

        /// <summary>Split on ';' while respecting Part 21 quoted strings ('' escape).</summary>
        private static IEnumerable<string> SplitStatements(string text)
        {
            var buf = new StringBuilder();
            bool inStr = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inStr)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') { buf.Append("''"); i++; continue; }
                        inStr = false;
                    }
                    buf.Append(c);
                }
                else if (c == '\'') { inStr = true; buf.Append(c); }
                else if (c == ';') { yield return buf.ToString(); buf.Clear(); }
                else buf.Append(c);
            }
            if (buf.Length > 0) yield return buf.ToString();
        }

        public int NextId() => _nextId++;

        public string TypeOf(int id)
            => Entities.TryGetValue(id, out var e) ? e.Key : null;

        public string ArgsOf(int id)
            => Entities.TryGetValue(id, out var e) ? e.Value : null;

        public List<int> ByType(string type)
        {
            var outIds = new List<int>();
            foreach (var kv in Entities)
                if (string.Equals(kv.Value.Key, type, StringComparison.OrdinalIgnoreCase))
                    outIds.Add(kv.Key);
            outIds.Sort();
            return outIds;
        }

        public List<int> Refs(int id)
        {
            var outIds = new List<int>();
            var args = ArgsOf(id);
            if (args == null) return outIds;
            foreach (Match m in RefRe.Matches(args))
                outIds.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return outIds;
        }

        /// <summary>First quoted string in an entity's arguments (its name).</summary>
        public string NameOf(int id)
        {
            var args = ArgsOf(id);
            if (args == null) return null;
            var m = Regex.Match(args, @"'((?:[^']|'')*)'");
            return m.Success ? m.Groups[1].Value.Replace("''", "'") : null;
        }

        private readonly List<string> _appended = new List<string>();

        /// <summary>
        /// Append a new entity, and register it so later passes can see it.
        ///
        /// Registration matters: de-instancing appends cloned PRODUCT entities,
        /// and the material pass afterwards looks products up by type. Without
        /// this the clones are invisible and only the original part gets a
        /// material.
        /// </summary>
        public void Append(string entityLine)
        {
            _appended.Add(entityLine);

            var m = EntityRe.Match(entityLine.TrimStart());
            if (!m.Success) return;
            int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string type = (m.Groups[2].Value ?? "").ToUpperInvariant();
            string body = entityLine.Trim();
            if (body.EndsWith(";", StringComparison.Ordinal))
                body = body.Substring(0, body.Length - 1);
            if (type.Length == 0)
            {
                var inner = Regex.Match(body, @"\(\s*([A-Z0-9_]+)\s*\(");
                type = "COMPLEX:" + (inner.Success ? inner.Groups[1].Value : "?");
                Entities[id] = new KeyValuePair<string, string>(
                    type, body.Substring(body.IndexOf('=') + 1));
            }
            else
            {
                Entities[id] = new KeyValuePair<string, string>(
                    type, body.Substring(m.Length - 1));
            }
            if (id >= _nextId) _nextId = id + 1;
        }

        /// <summary>Replace one entity's whole statement in the source text.</summary>
        public void Replace(int id, string newStatement)
        {
            var m = Regex.Match(Text, $@"^#{id}\s*=\s*.*?;",
                                RegexOptions.Multiline | RegexOptions.Singleline);
            if (!m.Success) return;
            Text = Text.Substring(0, m.Index) + newStatement + Text.Substring(m.Index + m.Length);
        }

        public void Save(string path)
        {
            string outText = Text;
            if (_appended.Count > 0)
            {
                int idx = outText.LastIndexOf("ENDSEC;", StringComparison.Ordinal);
                if (idx < 0) throw new InvalidDataException("no ENDSEC to append before");
                outText = outText.Substring(0, idx)
                        + string.Join(Environment.NewLine, _appended) + Environment.NewLine
                        + outText.Substring(idx);
            }
            File.WriteAllText(path, outText);
        }

        public static string Str(string s)
            => "'" + (s ?? "").Replace("'", "''") + "'";

        public static string Num(double v)
            => v.ToString("0.################", CultureInfo.InvariantCulture);
    }
}
