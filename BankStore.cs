using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ExamMachine
{
    public sealed class BankInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
    }

    public sealed class BankCatalog
    {
        public BankCatalog() { Banks = new List<BankInfo>(); }
        public List<BankInfo> Banks { get; set; }
        public string Active { get; set; }
    }

    internal sealed class BankStore
    {
        private static readonly UTF8Encoding EncodingForFiles = new UTF8Encoding(false);
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly string root;
        private readonly string catalogPath;
        internal BankCatalog Catalog { get; private set; }

        internal BankStore(string directory)
        {
            root = Path.Combine(directory, "题库数据");
            catalogPath = Path.Combine(root, "题库目录.json");
            Catalog = File.Exists(catalogPath) ? json.Deserialize<BankCatalog>(File.ReadAllText(catalogPath, EncodingForFiles)) : new BankCatalog();
            if (Catalog == null || Catalog.Banks == null) throw new InvalidDataException("题库目录文件格式有误。");
        }

        internal BankInfo Find(string id)
        {
            if (id == null || !Regex.IsMatch(id, @"\A[0-9a-f]{64}\z")) return null;
            return Catalog.Banks.FirstOrDefault(bank => bank.Id == id);
        }

        internal BankInfo Import(string source)
        {
            List<Question> bank = BankImport.Read(source);
            string content = json.Serialize(bank);
            string id;
            using (var sha = SHA256.Create())
                id = BitConverter.ToString(sha.ComputeHash(EncodingForFiles.GetBytes(content))).Replace("-", "").ToLowerInvariant();
            string directory = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
            WriteText(Path.Combine(directory, "题库.json"), content);
            var existing = Find(id);
            if (existing != null) return existing;
            var info = new BankInfo { Id = id, Name = Path.GetFileNameWithoutExtension(source), Count = bank.Count };
            var added = new BankCatalog { Active = Catalog.Active, Banks = new List<BankInfo>(Catalog.Banks) { info } };
            SaveCatalog(added);
            return info;
        }

        internal ExamData Open(BankInfo bank)
        {
            string directory = Path.Combine(root, bank.Id);
            var questions = json.Deserialize<List<Question>>(File.ReadAllText(Path.Combine(directory, "题库.json"), EncodingForFiles));
            if (questions == null || questions.Count != bank.Count) throw new InvalidDataException("题库数据不完整，请重新导入原文件。");
            return new ExamData(directory, questions);
        }

        internal void Select(BankInfo bank)
        {
            if (Catalog.Active != bank.Id) SaveCatalog(new BankCatalog { Active = bank.Id, Banks = Catalog.Banks });
        }

        private void SaveCatalog(BankCatalog catalog)
        {
            WriteText(catalogPath, json.Serialize(catalog));
            Catalog = catalog;
        }

        private static void WriteText(string path, string text)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, text, EncodingForFiles);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
