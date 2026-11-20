using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;

namespace ExamMachine
{
    internal static class BankImport
    {
        internal const string Letters = "ABCDEFGH";
        private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly Regex Ordinal = new Regex(@"^\s*(?:第\s*\d+\s*题\s*[:：]?|\d+[.．、)）]\s*)(.*)$");
        private static readonly Regex Choice = new Regex(@"^([A-Ha-hＡ-Ｈａ-ｈ])\s*[.．、:：)）]\s*(.+)$");
        private static readonly Regex Key = new Regex(@"^(?:正确答案|参考答案|答案|answer)\s*[:：]\s*(.*)$", RegexOptions.IgnoreCase);

        internal static List<Question> Read(string path)
        {
            var importer = new ImportRows();
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new FormatException("文件超过 64 MB，请拆分题库。");
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".csv":
                case ".tsv":
                    string delimiter = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? "\t" : ",";
                    using (var reader = new TextFieldParser(path, new UTF8Encoding(false, true), true))
                    {
                        reader.SetDelimiters(delimiter);
                        importer.Table(CsvRows(reader));
                    }
                    break;
                case ".xlsx": ReadWorkbook(path, importer); break;
                case ".docx": ReadDocument(path, importer); break;
                default: throw new FormatException("请选择 XLSX、DOCX、CSV 或 TSV 格式的题库。");
            }
            return importer.Finish();
        }

        private static IEnumerable<Row> CsvRows(TextFieldParser reader)
        {
            while (!reader.EndOfData)
            {
                long line = reader.LineNumber;
                string[] fields;
                try { fields = reader.ReadFields(); }
                catch (MalformedLineException) { throw new FormatException("第 " + line + " 行的 CSV 引号格式有误。"); }
                catch (DecoderFallbackException) { throw new FormatException("请将表格另存为 UTF-8 编码的 CSV 或 TSV。"); }
                yield return new Row("第 " + line + " 行", fields);
            }
        }

        private static XDocument Xml(ZipArchive archive, string name)
        {
            var entry = archive.GetEntry(name);
            if (entry == null) throw new FormatException("文件缺少表格或文档内容。");
            if (entry.Length > 32 * 1024 * 1024) throw new FormatException("单个工作表或文档内容过大，请拆分后导入。");
            using (var input = entry.Open())
            using (var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024
            })) return XDocument.Load(reader);
        }

        private static void ReadWorkbook(string path, ImportRows importer)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                string[] shared = archive.GetEntry("xl/sharedStrings.xml") == null ? new string[0] :
                    Xml(archive, "xl/sharedStrings.xml").Descendants(Sheet + "si")
                        .Select(item => string.Concat(item.Descendants(Sheet + "t").Select(t => t.Value))).ToArray();
                var sheets = archive.Entries.Where(entry => Regex.IsMatch(entry.FullName, @"^xl/worksheets/[^/]+\.xml$")).ToArray();
                for (int i = 0; i < sheets.Length; i++)
                {
                    string label = "工作表 " + (i + 1);
                    importer.Table(Xml(archive, sheets[i].FullName).Descendants(Sheet + "row").Select(row =>
                        new Row(label + " 第 " + (string)row.Attribute("r") + " 行", WorkbookRow(row, shared))));
                }
            }
        }

        private static string[] WorkbookRow(XElement row, string[] shared)
        {
            var result = new List<string>();
            foreach (var cell in row.Elements(Sheet + "c"))
            {
                int column = 0;
                foreach (char letter in ((string)cell.Attribute("r") ?? "").TakeWhile(char.IsLetter))
                    column = column * 26 + letter - 'A' + 1;
                if (column < 1 || column > 128) continue;
                while (result.Count < column) result.Add("");
                string type = (string)cell.Attribute("t");
                string value = (string)cell.Element(Sheet + "v") ?? "";
                int index;
                if (type == "s" && int.TryParse(value, out index) && index >= 0 && index < shared.Length) value = shared[index];
                else if (type == "inlineStr") value = string.Concat(cell.Descendants(Sheet + "t").Select(t => t.Value));
                else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                result[column - 1] = value;
            }
            return result.ToArray();
        }

        private static string WordText(XElement element)
        {
            return string.Concat(element.Descendants().Where(node => node.Name == Word + "t" || node.Name == Word + "br" || node.Name == Word + "tab")
                .Select(node => node.Name == Word + "br" ? "\n" : node.Name == Word + "tab" ? "\t" : node.Value));
        }

        private static void ReadDocument(string path, ImportRows importer)
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                XElement body = Xml(archive, "word/document.xml").Descendants(Word + "body").FirstOrDefault();
                if (body == null) throw new FormatException("Word 文档没有正文。");
                int table = 0;
                foreach (var grid in body.Elements(Word + "tbl"))
                {
                    string label = "Word 表格 " + (++table);
                    importer.Table(grid.Elements(Word + "tr").Select((row, index) => new Row(label + " 第 " + (index + 1) + " 行",
                        row.Elements(Word + "tc").Select(cell => string.Join("\n", cell.Elements(Word + "p").Select(WordText))).ToArray())));
                }
                var lines = body.Elements(Word + "p").SelectMany(paragraph => WordText(paragraph).Split('\n'));
                ReadWordLines(lines, importer);
            }
        }

        private static void ReadWordLines(IEnumerable<string> lines, ImportRows importer)
        {
            string kind = null, location = null;
            var stem = new List<string>();
            var options = new string[Letters.Length];
            int position = -1, line = 0;
            foreach (string raw in lines)
            {
                line++;
                string text = raw.Trim();
                if (text.Length == 0) continue;
                var section = Regex.Match(text, @"^(单选题|多选题|判断题)(?:\s*[（(].*[）)])?\s*$");
                if (section.Success) { kind = section.Groups[1].Value; continue; }
                var number = Ordinal.Match(text);
                if (number.Success)
                {
                    if (location != null) importer.Error(location, "缺少答案行。");
                    location = "Word 第 " + line + " 行";
                    stem.Clear();
                    Array.Clear(options, 0, options.Length);
                    position = -1;
                    text = number.Groups[1].Value.Trim();
                }
                if (location == null || text.Length == 0) continue;
                var answer = Key.Match(text);
                if (answer.Success)
                {
                    importer.Add(location, kind, string.Join("\n", stem), options, answer.Groups[1].Value);
                    location = null;
                    continue;
                }
                var choice = Choice.Match(text);
                if (choice.Success)
                {
                    position = Letters.IndexOf(Normalize(choice.Groups[1].Value));
                    options[position] = choice.Groups[2].Value;
                }
                else if (kind == "判断题" && Truth(text) != 0 && position < 1) options[++position] = text;
                else if (position >= 0) options[position] += "\n" + text;
                else stem.Add(text);
            }
            if (location != null) importer.Error(location, "缺少答案行。");
        }

        private sealed class Row
        {
            internal readonly string Line;
            internal readonly string[] Values;
            internal Row(string line, string[] values) { Line = line; Values = values ?? new string[0]; }
        }

        private sealed class ImportRows
        {
            private readonly List<Question> questions = new List<Question>();
            private readonly List<string> errors = new List<string>();
            private int invalid;
            private long characters;

            internal void Table(IEnumerable<Row> rows)
            {
                string[] headers = null;
                foreach (Row row in rows)
                {
                    var mapped = row.Values.Select(Header).ToArray();
                    if (mapped.Contains("PROMPT") && mapped.Contains("ANSWER")) { headers = mapped; continue; }
                    if (headers == null || row.Values.All(string.IsNullOrWhiteSpace)) continue;
                    Func<string, string> cell = name =>
                    {
                        int index = Array.IndexOf(headers, name);
                        return index >= 0 && index < row.Values.Length ? row.Values[index] : "";
                    };
                    string prompt = cell("PROMPT"), answer = cell("ANSWER");
                    if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(answer)) continue;
                    Add(row.Line, cell("KIND"), prompt, Letters.Select(letter => cell(letter.ToString())).ToArray(), answer);
                }
            }

            internal void Add(string location, string type, string stem, string[] values, string answer)
            {
                try
                {
                    string prompt = (stem ?? "").Trim();
                    if (prompt.Length == 0) throw new FormatException("缺少题干。");
                    if (string.IsNullOrWhiteSpace(answer)) throw new FormatException("缺少正确答案。");
                    string kind = Kind(type);
                    var options = values.Select(value => (value ?? "").Trim()).ToArray();
                    int last = Array.FindLastIndex(options, text => text.Length > 0);
                    options = options.Take(last + 1).ToArray();
                    if (options.Length == 0 && (kind == "判断题" || Truth(answer) != 0)) options = new[] { "正确", "错误" };
                    if (options.Length < 2 || options.Any(string.IsNullOrWhiteSpace))
                        throw new FormatException("请提供至少两个连续选项，从 A 开始填写。");
                    int mask = Answer(answer, options, kind);
                    bool multiple = (mask & (mask - 1)) != 0;
                    if (kind == null) kind = options.Length == 2 && Truth(options[0]) * Truth(options[1]) == -1 ? "判断题" : multiple ? "多选题" : "单选题";
                    if (kind == "判断题" && options.Length != 2) throw new FormatException("判断题需要两个选项。");
                    if (kind != "多选题" && multiple) throw new FormatException("单选题或判断题只能有一个正确选项。");
                    characters += prompt.Length + options.Sum(text => text.Length);
                    if (questions.Count >= 10000 || characters > 5000000) throw new ImportLimitException();
                    int number = questions.Count + 1;
                    questions.Add(new Question { Id = "q-" + number.ToString(CultureInfo.InvariantCulture), Number = number,
                        Kind = kind, Prompt = prompt, Options = options, Correct = mask });
                }
                catch (FormatException error) { Error(location, error.Message); }
            }

            internal void Error(string location, string error)
            {
                invalid++;
                if (errors.Count < 8) errors.Add(location + "  " + error);
            }

            internal List<Question> Finish()
            {
                if (invalid > 0) throw new FormatException("有 " + invalid + " 道题需要修正。\n" + string.Join("\n", errors));
                if (questions.Count == 0) throw new FormatException("没有识别到题目。请检查「题干」「正确答案」列名，或使用带编号和答案行的 Word 格式。");
                return questions;
            }
        }

        private sealed class ImportLimitException : InvalidOperationException
        {
            internal ImportLimitException() : base("题库超过 10,000 题或正文过长，请拆分后导入。") { }
        }

        private static string Normalize(string text)
        {
            return (text ?? "").Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        }

        private static string Header(string raw)
        {
            string label = Regex.Replace(Normalize(raw), @"[\s_\-]", "");
            if (new[] { "题干", "题目", "问题", "PROMPT", "QUESTION", "STEM" }.Contains(label)) return "PROMPT";
            if (new[] { "正确答案", "答案", "参考答案", "ANSWER", "CORRECTANSWER" }.Contains(label)) return "ANSWER";
            if (new[] { "题型", "类型", "TYPE", "KIND" }.Contains(label)) return "KIND";
            label = Regex.Replace(label, @"^(选项|OPTION)", "");
            return label.Length == 1 && Letters.Contains(label) ? label : null;
        }

        private static string Kind(string text)
        {
            switch (Normalize(text))
            {
                case "": return null;
                case "单选题": case "单项选择题": case "单选": case "SINGLE": return "单选题";
                case "多选题": case "多项选择题": case "多选": case "MULTI": case "MULTIPLE": return "多选题";
                case "判断题": case "判断": case "是非题": case "TRUE/FALSE": case "TF": return "判断题";
                default: throw new FormatException("无法识别题型「" + text + "」。");
            }
        }

        private static int Truth(string text)
        {
            switch (Normalize(text))
            {
                case "正确": case "此题正确": case "对": case "是": case "TRUE": case "√": return 1;
                case "错误": case "此题错误": case "错": case "否": case "FALSE": case "×": return -1;
                default: return 0;
            }
        }

        private static int Answer(string text, string[] options, string kind)
        {
            string answer = Normalize(text);
            string letters = Regex.Replace(answer, @"[\s,、;/+|]+", "");
            if (Regex.IsMatch(letters, @"^[A-H]+$") && LettersFit(letters, options.Length))
                return LetterMask(letters);
            int exact = Array.FindIndex(options, option => Normalize(option) == answer);
            if (exact >= 0) return 1 << exact;
            int truth = Truth(answer);
            if (truth != 0)
            {
                exact = Array.FindIndex(options, option => Truth(option) == truth);
                if (exact >= 0) return 1 << exact;
            }
            var prefix = Regex.Match(answer, @"^([A-H])\s*[.、:)]");
            if (prefix.Success && (kind == "单选题" || kind == "判断题")) letters = prefix.Groups[1].Value;
            else letters = string.Concat(Regex.Matches(answer, @"(?:^|\r?\n\s*|[ \t]{2,}|[;,]\s*)([A-H])\s*[.、:)]")
                .Cast<Match>().Select(match => match.Groups[1].Value));
            if (!LettersFit(letters, options.Length))
                throw new FormatException("答案应为有效选项字母（如 A、AC），或与选项一致的文字。");
            return LetterMask(letters);
        }

        private static bool LettersFit(string letters, int optionCount)
        {
            return letters.Length > 0 && letters.All(letter => Letters.IndexOf(letter) < optionCount);
        }

        private static int LetterMask(string letters)
        {
            return letters.Aggregate(0, (mask, letter) => mask | (1 << Letters.IndexOf(letter)));
        }
    }
}
