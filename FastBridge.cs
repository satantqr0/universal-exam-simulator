using System;
using System.Collections.Generic;
using System.Linq;

namespace ExamMachine
{
    public sealed class BrowserRequest
    {
        public int Id { get; set; }
        public string Action { get; set; }
        public int Count { get; set; }
        public int Index { get; set; }
        public int Mask { get; set; }
        public int Pool { get; set; }
        public int Rating { get; set; }
        public string Kind { get; set; }
        public string Filter { get; set; }
        public string QuestionId { get; set; }
        public string BankId { get; set; }
        public StudyNote Note { get; set; }
        public List<PalacePlace> Places { get; set; }
    }

    internal sealed class FastBridge
    {
        private const int PageSize = 42;
        private readonly string directory;
        private BankStore banks;
        private BankInfo activeBank;
        private ExamData data;
        private Score cumulative;
        private object dashboard;
        private long dashboardRevision = -1;
        private DateTime dashboardDate;

        internal FastBridge(string directory) { this.directory = directory; }

        internal void WarmUp()
        {
            banks = new BankStore(directory);
            BankInfo bank = banks.Find(banks.Catalog.Active);
            if (bank != null) Activate(bank);
        }

        internal void CheckpointDraft()
        {
            if (data != null) data.CheckpointDraft();
        }

        private void Activate(BankInfo bank)
        {
            if (activeBank != null && activeBank.Id == bank.Id) return;
            CheckpointDraft();
            ExamData selected = banks.Open(bank);
            banks.Select(bank);
            data = selected;
            activeBank = bank;
            dashboard = null;
            cumulative = null;
        }

        private object Workspace()
        {
            return new
            {
                Banks = banks.Catalog.Banks, ActiveBank = activeBank,
                Questions = data == null ? null : data.Bank.Select(q => new { q.Id, q.Kind, q.Number, q.Prompt, q.Options }).ToArray(),
                Kinds = ExamData.Kinds, ExamData.HalfPoints, Dashboard = data == null ? null : Dashboard()
            };
        }

        internal object Dispatch(BrowserRequest request, string importPath = null)
        {
            switch (request.Action)
            {
                case "boot": return Workspace();
                case "import-bank":
                    if (importPath == null) return null;
                    Activate(banks.Import(importPath));
                    return Workspace();
                case "select-bank":
                    BankInfo bank = banks.Find(request.BankId);
                    if (bank == null) throw new ArgumentException("找不到所选题库。");
                    Activate(bank);
                    return Workspace();
            }
            if (data == null) throw new InvalidOperationException("请先导入题库。");
            switch (request.Action)
            {
                case "dashboard": return Dashboard();
                case "start":
                    if (request.Pool < 0 || request.Pool > 2) throw new ArgumentException("抽题模式无效。");
                    if (request.Kind != null && !ExamData.Kinds.Contains(request.Kind)) throw new ArgumentException("题型无效。");
                    data.StartExam(request.Count, request.Kind, (QuestionPool)request.Pool);
                    return data.State.Draft;
                case "retry":
                    IEnumerable<string> misses = request.Index < 0 ? data.Pending() :
                        HistoryAttempt(request.Index).Items.Where(item => data.Status(item) != "答对").Select(item => item.QuestionId);
                    data.StartRetry(misses);
                    return data.State.Draft;
                case "resume-timer":
                    long elapsed = data.ResumeDraftTimer();
                    return new { ElapsedMilliseconds = elapsed, data.State.Draft.PartialTiming };
                case "save-timer": data.SaveDraftTimer(); return true;
                case "pause-timer": data.PauseDraftTimer(); return true;
                case "answer":
                    if (data.State.Draft == null || data.State.Draft.Cursor != request.Index)
                        throw new InvalidOperationException("答题位置已变化，请重新作答。");
                    data.ChangeAnswer(request.Mask, true);
                    return data.State.Draft.Cursor;
                case "move": data.MoveTo(request.Index); return true;
                case "flag": data.ToggleFlag(); return true;
                case "submit":
                    var attempt = data.Submit();
                    var score = data.Summarize(attempt.Items);
                    cumulative.Correct += score.Correct;
                    cumulative.Wrong += score.Wrong;
                    cumulative.Blank += score.Blank;
                    cumulative.EarnedHalfPoints += score.EarnedHalfPoints;
                    cumulative.PossibleHalfPoints += score.PossibleHalfPoints;
                    return Result(attempt, 0, score);
                case "history": return History(request.Index);
                case "result": return Result(HistoryAttempt(request.Index), request.Index);
                case "mistakes": return Mistakes(request.Index, request.Filter);
                case "memory": return Memory();
                case "deck":
                    string[] ids = request.Filter == "due" ? data.Due(DateTime.Today) :
                        request.Filter == "difficult" ? data.DifficultCards() : data.NewCards(20);
                    return ids.Select(id => new { Id = id, Mask = data.Questions[id].Correct }).ToArray();
                case "key": return data.Questions[request.QuestionId].Correct;
                case "rate":
                    if (request.Rating < 0 || request.Rating > 2) throw new ArgumentException("自评无效。");
                    return data.Rate(request.QuestionId, (RecallRating)request.Rating, DateTime.Today);
                case "notes": return data.State.Notes;
                case "save-note":
                    if (request.Note == null || string.IsNullOrWhiteSpace(request.Note.Topic)) throw new ArgumentException("请填写复盘主题。");
                    if (string.IsNullOrEmpty(request.Note.Id)) request.Note.Id = Guid.NewGuid().ToString("N");
                    data.SaveNote(request.Note);
                    return request.Note;
                case "palace": return data.State.Palace;
                case "save-palace":
                    if (request.Places == null || request.Places.Count > 20) throw new ArgumentException("记忆宫殿最多支持 20 站。");
                    if (request.Places.Any(place => string.IsNullOrWhiteSpace(place.Cue) && !string.IsNullOrWhiteSpace(place.Scene)))
                        throw new ArgumentException("有联结内容的行请先填写位置。");
                    data.SavePalace(request.Places);
                    return true;
                default: throw new ArgumentException("操作无效。");
            }
        }

        private object Dashboard()
        {
            if (dashboard != null && dashboardRevision == data.Revision && dashboardDate == DateTime.Today) return dashboard;
            var state = data.State;
            if (cumulative == null) cumulative = data.Summarize(state.History.SelectMany(attempt => attempt.Items));
            var pools = Enumerable.Range(0, 3).Select(pool => new string[] { null }.Concat(ExamData.Kinds)
                .Select(kind => data.QuestionIds(kind, (QuestionPool)pool).Count()).ToArray()).ToArray();
            dashboard = new
            {
                Total = data.Bank.Count,
                HistoryCount = state.History.Count,
                Last = state.History.Count == 0 ? "—" : data.Summarize(state.History[0].Items).Accuracy,
                Overall = cumulative.Accuracy,
                Pending = data.Pending().Count(),
                Due = data.Due(DateTime.Today).Length,
                Draft = data.State.Draft,
                Pools = pools,
                PoolPlans = pools.Select(pool => Enumerable.Range(0, Math.Min(ExamData.Max, pool[0]) + 1)
                    .Select(count => ExamData.PlanForPool(count, pool.Skip(1).ToArray())).ToArray()).ToArray()
            };
            dashboardRevision = data.Revision;
            dashboardDate = DateTime.Today;
            return dashboard;
        }

        private Attempt HistoryAttempt(int index)
        {
            if (index < 0 || index >= data.State.History.Count) throw new ArgumentException("练习记录已刷新，请重新选择。");
            return data.State.History[index];
        }

        private object History(int offset)
        {
            int total = data.State.History.Count;
            offset = Math.Max(0, Math.Min(offset, Math.Max(0, total - 1) / PageSize * PageSize));
            var rows = data.State.History.Skip(offset).Take(PageSize).Select((attempt, index) => new
            {
                Index = index + offset, attempt.Mode,
                Started = LocalTimestamp(attempt.StartedUtc, "yyyy-MM-dd HH:mm"),
                Date = LocalTimestamp(attempt.FinishedUtc, "yyyy-MM-dd HH:mm"),
                attempt.ElapsedMilliseconds, attempt.PartialTiming,
                Score = data.Summarize(attempt.Items)
            }).ToArray();
            return new { Rows = rows, Total = total, Offset = offset, PageSize };
        }

        private object Result(Attempt attempt, int index, Score score = null)
        {
            return new
            {
                Index = index, attempt.Mode, Score = score ?? data.Summarize(attempt.Items),
                StartedAt = LocalTimestamp(attempt.StartedUtc, "yyyy-MM-dd HH:mm:ss"),
                FinishedAt = LocalTimestamp(attempt.FinishedUtc, "yyyy-MM-dd HH:mm:ss"),
                attempt.ElapsedMilliseconds, attempt.PartialTiming,
                ByKind = ExamData.Kinds.Select(kind => new
                {
                    Kind = kind, Score = data.Summarize(attempt.Items.Where(item => data.Questions[item.QuestionId].Kind == kind))
                }).Where(entry => entry.Score.Total > 0).ToArray(),
                Rows = attempt.Items.Select((item, i) => new
                {
                    Index = i + 1, item.QuestionId, item.Answer, data.Questions[item.QuestionId].Correct, Status = data.Status(item)
                }).ToArray()
            };
        }

        private static string LocalTimestamp(DateTime? utc, string format)
        {
            if (!utc.HasValue || utc.Value == DateTime.MinValue) return "—";
            return utc.Value.ToLocalTime().ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }

        private object Mistakes(int offset, string filter)
        {
            var all = data.State.Mistakes;
            var rows = all.Where(entry => filter == "all" || entry.Value.Pending == (filter != "mastered"))
                .OrderByDescending(entry => entry.Value.Total).ThenByDescending(entry => entry.Value.LastMissUtc).ToArray();
            offset = Math.Max(0, Math.Min(offset, Math.Max(0, rows.Length - 1) / PageSize * PageSize));
            return new
            {
                Pending = all.Count(entry => entry.Value.Pending), Wrong = all.Values.Sum(entry => entry.Wrong), Blank = all.Values.Sum(entry => entry.Blank),
                Rows = rows.Skip(offset).Take(PageSize).Select(entry => new
                {
                    QuestionId = entry.Key, entry.Value.Pending, entry.Value.Wrong, entry.Value.Blank,
                    Answer = entry.Value.LastAnswer, data.Questions[entry.Key].Correct
                }).ToArray(),
                Total = rows.Length, Offset = offset, PageSize
            };
        }

        private object Memory()
        {
            return new
            {
                Due = data.Due(DateTime.Today).Length,
                Learned = data.State.Cards.Count,
                Finished = data.State.Cards.Values.Count(card => card.DueDay == null),
                New = data.Bank.Count - data.State.Cards.Count,
                Difficult = data.DifficultCards().Length
            };
        }
    }
}
