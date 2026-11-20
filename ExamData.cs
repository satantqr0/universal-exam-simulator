using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ExamMachine
{
    public sealed class Question
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public int Number { get; set; }
        public string Prompt { get; set; }
        public string[] Options { get; set; }
        public int Correct { get; set; }

        public static string OptionLabel(Question question, int index)
        {
            return ((char)('A' + index)) + ".  " + question.Options[index];
        }

        public string AnswerText(int mask)
        {
            if (mask == 0) return "未作答";
            return string.Join(Environment.NewLine, Enumerable.Range(0, Options.Length)
                .Where(i => (mask & (1 << i)) != 0).Select(i => OptionLabel(this, i)));
        }
    }

    public sealed class Item
    {
        public string QuestionId { get; set; }
        public int Answer { get; set; }
        public bool Flag { get; set; }
    }

    public sealed class Attempt
    {
        public string Mode { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public long? ElapsedMilliseconds { get; set; }
        public bool PartialTiming { get; set; }
        public int Cursor { get; set; }
        public List<Item> Items { get; set; }
    }

    public sealed class Mistake
    {
        public int Wrong { get; set; }
        public int Blank { get; set; }
        public int LastAnswer { get; set; }
        public DateTime LastMissUtc { get; set; }
        public bool Pending { get; set; }
        public int Total { get { return Wrong + Blank; } }
    }

    public sealed class Profile
    {
        public Profile()
        {
            History = new List<Attempt>();
            Mistakes = new Dictionary<string, Mistake>();
            Cards = new Dictionary<string, MemoryCard>();
            Notes = new List<StudyNote>();
            Palace = new List<PalacePlace>();
        }

        public Attempt Draft { get; set; }
        public string DraftKey { get; set; }
        public List<Attempt> History { get; set; }
        public Dictionary<string, Mistake> Mistakes { get; set; }
        public Dictionary<string, MemoryCard> Cards { get; set; }
        public List<StudyNote> Notes { get; set; }
        public List<PalacePlace> Palace { get; set; }
    }

    internal sealed class Score
    {
        public int Correct { get; set; }
        public int Wrong { get; set; }
        public int Blank { get; set; }
        public int EarnedHalfPoints { get; set; }
        public int PossibleHalfPoints { get; set; }
        public decimal PointsOutOf100
        {
            get { return PossibleHalfPoints == 0 ? 0 : Math.Round(100m * EarnedHalfPoints / PossibleHalfPoints, 2, MidpointRounding.AwayFromZero); }
        }
        public int Total { get { return Correct + Wrong + Blank; } }
        public string Accuracy { get { return Total == 0 ? "—" : (100.0 * Correct / Total).ToString("0.#") + "%"; } }
    }

    internal enum QuestionPool { All, Unanswered, Answered }

    internal sealed partial class ExamData
    {
        internal static readonly string[] Kinds = { "单选题", "多选题", "判断题" };
        internal static readonly int[] HalfPoints = { 2, 4, 1 };
        private static readonly int[] MixPercentages = { 70, 10, 20 };
        internal const int Min = 10;
        internal const int Max = 100;
        internal static readonly int[][] MixedPlans = Enumerable.Range(0, Max + 1).Select(MixedPlan).ToArray();
        internal const string ExamMode = "模拟考试";
        internal const string RetryMode = "错题重测";
        internal const string NewMode = "未做题模拟";
        internal const string ConsolidationMode = "巩固模拟";

        private readonly Random random = new Random();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly Encoding ProfileEncoding = new UTF8Encoding(false);
        private readonly string profilePath;
        private readonly string draftPath;
        private Stopwatch draftClock;
        private long elapsedBeforeSession;
        private bool hasDraftChanges;
        private string archiveBody;
        private string historyBody;
        private HashSet<string> answeredQuestions;
        internal readonly List<Question> Bank;
        internal readonly Dictionary<string, Question> Questions;
        internal Profile State { get; private set; }
        internal long Revision { get; private set; }
        internal long ResultRevision { get; private set; }
        internal long StudyRevision { get; private set; }

        internal ExamData(string directory, List<Question> questions)
        {
            Bank = questions;
            Questions = Bank.ToDictionary(q => q.Id);
            profilePath = Path.Combine(directory, "练习记录.json");
            draftPath = Path.Combine(directory, "练习进度.json");
            State = File.Exists(profilePath)
                ? json.Deserialize<Profile>(File.ReadAllText(profilePath, Encoding.UTF8))
                : new Profile();
            if (State == null || State.History == null || State.Mistakes == null)
                throw new InvalidDataException("练习记录格式有误。");
            RestoreDraft();
        }

        public sealed class DraftSnapshot
        {
            public string Key { get; set; }
            public Attempt Draft { get; set; }
        }

        private void RestoreDraft()
        {
            if (draftPath == null || State.Draft == null || State.DraftKey == null || !File.Exists(draftPath)) return;
            var snapshot = json.Deserialize<DraftSnapshot>(File.ReadAllText(draftPath, Encoding.UTF8));
            if (snapshot != null && snapshot.Key == State.DraftKey && snapshot.Draft != null)
            {
                State.Draft = snapshot.Draft;
                hasDraftChanges = true;
            }
        }

        internal void CheckpointDraft()
        {
            if (draftClock == null && !hasDraftChanges) return;
            CaptureDraftTime();
            WriteDraft(true);
            draftClock = null;
        }

        internal long ResumeDraftTimer()
        {
            var attempt = State.Draft;
            if (attempt == null) throw new InvalidOperationException("当前没有可继续的练习。");
            if (draftClock == null)
            {
                if (!attempt.ElapsedMilliseconds.HasValue) attempt.PartialTiming = true;
                elapsedBeforeSession = attempt.ElapsedMilliseconds.GetValueOrDefault();
                draftClock = Stopwatch.StartNew();
            }
            return elapsedBeforeSession + draftClock.ElapsedMilliseconds;
        }

        internal void SaveDraftTimer()
        {
            if (draftClock != null) SaveDraft();
        }

        internal void PauseDraftTimer()
        {
            SaveDraftTimer();
            draftClock = null;
        }

        private void CaptureDraftTime()
        {
            if (draftClock != null) State.Draft.ElapsedMilliseconds = elapsedBeforeSession + draftClock.ElapsedMilliseconds;
        }

        internal void Save()
        {
            string archive = SerializeArchive();
            WriteProfile(archive);
            archiveBody = archive;
        }

        private string SerializeArchive()
        {
            if (historyBody == null) historyBody = json.Serialize(State.History);
            return "\"History\":" + historyBody + ',' + json.Serialize(new { State.Mistakes, State.Cards, State.Notes, State.Palace }).Substring(1);
        }

        private void SaveDraft()
        {
            CaptureDraftTime();
            WriteDraft(false);
        }

        private void WriteDraft(bool checkpoint)
        {
            if (draftPath != null && State.DraftKey != null && !checkpoint)
            {
                WriteAtomically(draftPath, file => file.Write(json.Serialize(new DraftSnapshot { Key = State.DraftKey, Draft = State.Draft })));
                hasDraftChanges = true;
                Revision++;
                return;
            }
            if (archiveBody == null) archiveBody = SerializeArchive();
            WriteProfile(archiveBody);
        }

        private void WriteProfile(string archive)
        {
            // Rotate the key in the same commit as the profile. Any older snapshot is obsolete even if cleanup fails.
            string key = draftPath != null && State.Draft != null ? Guid.NewGuid().ToString("N") : null;
            WriteAtomically(profilePath, file =>
            {
                file.Write("{\"Draft\":");
                file.Write(json.Serialize(State.Draft));
                if (key != null) { file.Write(",\"DraftKey\":"); file.Write(json.Serialize(key)); }
                file.Write(',');
                file.Write(archive);
            });
            State.DraftKey = key;
            hasDraftChanges = false;
            Revision++;
            if (draftPath != null)
            {
                try { File.Delete(draftPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void WriteAtomically(string path, Action<StreamWriter> write)
        {
            string pending = path + ".tmp";
            using (var file = new StreamWriter(pending, false, ProfileEncoding)) write(file);
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }

        internal IEnumerable<string> Pending()
        {
            return State.Mistakes.Where(pair => pair.Value.Pending).Select(pair => pair.Key);
        }

        internal IEnumerable<string> QuestionIds(string kind, QuestionPool pool)
        {
            IEnumerable<Question> candidates = Bank.Where(question => kind == null || question.Kind == kind);
            if (pool != QuestionPool.All)
            {
                if (answeredQuestions == null)
                    answeredQuestions = new HashSet<string>(State.History.SelectMany(attempt => attempt.Items)
                        .Where(item => item.Answer != 0).Select(item => item.QuestionId));
                candidates = candidates.Where(question => answeredQuestions.Contains(question.Id) == (pool == QuestionPool.Answered));
            }
            return candidates.Select(question => question.Id);
        }

        internal void StartExam(int count, string kind, QuestionPool pool = QuestionPool.All)
        {
            if (count < 1 || count > Max) throw new ArgumentOutOfRangeException("count");
            string[] ids = QuestionIds(kind, pool).ToArray();
            if (count > ids.Length || (count < Min && count != ids.Length))
                throw new ArgumentOutOfRangeException("count");
            string mode = pool == QuestionPool.Answered ? ConsolidationMode : pool == QuestionPool.Unanswered ? NewMode : ExamMode;
            if (kind != null)
            {
                Start(mode, Sample(ids, count));
                return;
            }

            var byKind = ids.ToLookup(id => Questions[id].Kind);
            int[] plan = PlanForPool(count, Kinds.Select(type => byKind[type].Count()).ToArray());
            var selected = Kinds.SelectMany((type, index) => Sample(byKind[type], plan[index]));
            Start(mode, Sample(selected, count));
        }

        internal void StartRetry(IEnumerable<string> misses)
        {
            var candidates = misses.Distinct().ToArray();
            Start(RetryMode, Sample(candidates, Math.Min(Max, candidates.Length)));
        }

        private static int[] MixedPlan(int count)
        {
            int[] plan = MixPercentages.Select(percentage => count * percentage / 100).ToArray();
            int remainder = count - plan.Sum();
            var ordered = Enumerable.Range(0, Kinds.Length)
                .OrderByDescending(index => count * MixPercentages[index] % 100);
            foreach (int index in ordered.Take(remainder)) plan[index]++;
            return plan;
        }

        internal static int[] PlanForPool(int count, int[] capacities)
        {
            int[] plan = MixedPlans[count].Select((quota, index) => Math.Min(quota, capacities[index])).ToArray();
            int missing = count - plan.Sum();
            while (missing-- > 0)
            {
                int category = Enumerable.Range(0, Kinds.Length).Where(index => plan[index] < capacities[index])
                    .OrderByDescending(index => count * MixPercentages[index] - plan[index] * 100).First();
                plan[category]++;
            }
            return plan;
        }

        private string[] Sample(IEnumerable<string> candidates, int count)
        {
            var ids = candidates.ToArray();
            for (int i = 0; i < count; i++)
            {
                int j = random.Next(i, ids.Length);
                string id = ids[j];
                ids[j] = ids[i];
                ids[i] = id;
            }
            return count == ids.Length ? ids : ids.Take(count).ToArray();
        }

        private void Start(string mode, string[] ids)
        {
            if (ids.Length == 0) throw new InvalidOperationException("没有可抽取的题目。");
            var items = ids.Select(id => new Item { QuestionId = id }).ToList();
            Attempt oldDraft = State.Draft;
            State.Draft = new Attempt { Mode = mode, StartedUtc = DateTime.UtcNow, Items = items, ElapsedMilliseconds = 0 };
            try { WriteDraft(true); }
            catch { State.Draft = oldDraft; throw; }
            draftClock = null;
        }

        internal void ChangeAnswer(int mask, bool autoAdvance = false)
        {
            var attempt = State.Draft;
            int position = attempt.Cursor;
            var item = attempt.Items[position];
            var question = Questions[item.QuestionId];
            bool multi = question.Kind == "多选题";
            if (mask < 0 || mask >= (1 << question.Options.Length) ||
                (!multi && (mask & (mask - 1)) != 0))
                throw new ArgumentOutOfRangeException("mask");
            int oldAnswer = item.Answer;
            item.Answer = mask;
            if (autoAdvance && mask != 0 && !multi && position + 1 < attempt.Items.Count)
                attempt.Cursor = position + 1;
            try { SaveDraft(); }
            catch
            {
                item.Answer = oldAnswer;
                attempt.Cursor = position;
                throw;
            }
        }

        internal void MoveTo(int index)
        {
            if (index < 0 || index >= State.Draft.Items.Count) throw new ArgumentOutOfRangeException("index");
            int cursor = State.Draft.Cursor;
            State.Draft.Cursor = index;
            try { SaveDraft(); }
            catch { State.Draft.Cursor = cursor; throw; }
        }

        internal void ToggleFlag()
        {
            var item = State.Draft.Items[State.Draft.Cursor];
            item.Flag = !item.Flag;
            try { SaveDraft(); }
            catch { item.Flag = !item.Flag; throw; }
        }

        internal string Status(Item item)
        {
            if (item.Answer == 0) return "未作答";
            return item.Answer == Questions[item.QuestionId].Correct ? "答对" : "答错";
        }

        internal Score Summarize(IEnumerable<Item> items)
        {
            var score = new Score();
            foreach (Item item in items)
            {
                Question question = Questions[item.QuestionId];
                int halfPoints = HalfPoints[Array.IndexOf(Kinds, question.Kind)];
                score.PossibleHalfPoints += halfPoints;
                if (item.Answer == 0) score.Blank++;
                else if (item.Answer == question.Correct)
                {
                    score.Correct++;
                    score.EarnedHalfPoints += halfPoints;
                }
                else score.Wrong++;
            }
            return score;
        }

        internal Attempt Submit()
        {
            Attempt attempt = State.Draft;
            CaptureDraftTime();
            var previousMistakes = State.Mistakes;
            var updatedMistakes = new Dictionary<string, Mistake>(previousMistakes);
            DateTime now = DateTime.UtcNow;
            foreach (Item item in attempt.Items)
            {
                Mistake old;
                updatedMistakes.TryGetValue(item.QuestionId, out old);
                bool correct = item.Answer == Questions[item.QuestionId].Correct;
                if (correct && old == null) continue;
                if (old == null) old = new Mistake();

                updatedMistakes[item.QuestionId] = new Mistake
                {
                    Pending = !correct,
                    Wrong = old.Wrong + (!correct && item.Answer != 0 ? 1 : 0),
                    Blank = old.Blank + (item.Answer == 0 ? 1 : 0),
                    LastMissUtc = correct ? old.LastMissUtc : now,
                    LastAnswer = correct ? old.LastAnswer : item.Answer
                };
            }
            attempt.FinishedUtc = now;
            State.History.Insert(0, attempt);
            State.Draft = null;
            State.Mistakes = updatedMistakes;
            string previousHistory = historyBody;
            historyBody = null;
            try { Save(); }
            catch
            {
                historyBody = previousHistory;
                State.Mistakes = previousMistakes;
                State.Draft = attempt;
                State.History.RemoveAt(0);
                attempt.FinishedUtc = null;
                throw;
            }
            if (answeredQuestions != null)
                answeredQuestions.UnionWith(attempt.Items.Where(item => item.Answer != 0).Select(item => item.QuestionId));
            draftClock = null;
            ResultRevision = StudyRevision = Revision;
            return attempt;
        }
    }
}
