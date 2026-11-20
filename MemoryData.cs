using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ExamMachine
{
    public sealed class MemoryCard
    {
        public string FirstDay { get; set; }
        public int Step { get; set; }
        public string DueDay { get; set; }
        public int Reviews { get; set; }
        public int Recalled { get; set; }
        public int LastRating { get; set; }
    }

    public sealed class StudyNote
    {
        public string Id { get; set; }
        public string QuestionId { get; set; }
        public string Topic { get; set; }
        public string Recall { get; set; }
        public string Gaps { get; set; }
        public string Chunks { get; set; }
        public string Mnemonic { get; set; }
        public string Explanation { get; set; }
        public string StickingPoint { get; set; }
        public string Analogy { get; set; }
    }

    public sealed class PalacePlace
    {
        public string Cue { get; set; }
        public string Scene { get; set; }
    }

    internal enum RecallRating { Forgot, Fuzzy, Recalled }

    internal sealed partial class ExamData
    {
        private static readonly int[] ReviewDays = { 1, 3, 7, 16, 35 };

        internal string[] Due(DateTime date)
        {
            string day = Day(date);
            return State.Cards.Where(c => c.Value.DueDay != null && string.CompareOrdinal(c.Value.DueDay, day) <= 0)
                .OrderBy(c => c.Value.DueDay).ThenBy(c => c.Value.LastRating).Select(c => c.Key).ToArray();
        }

        internal string[] NewCards(int max)
        {
            var active = new HashSet<string>(Pending());
            return Bank.Where(q => !State.Cards.ContainsKey(q.Id)).OrderByDescending(q => active.Contains(q.Id))
                .ThenBy(q => random.Next()).Take(max).Select(q => q.Id).ToArray();
        }

        internal string[] DifficultCards()
        {
            return Pending().OrderByDescending(id => State.Mistakes[id].Total)
                .Concat(State.Cards.Where(c => c.Value.LastRating < (int)RecallRating.Recalled)
                    .OrderBy(c => c.Value.DueDay).Select(c => c.Key))
                .Distinct().Take(Max).ToArray();
        }

        internal MemoryCard Rate(string id, RecallRating rating, DateTime date)
        {
            if (!Questions.ContainsKey(id)) throw new ArgumentException("Unknown question", "id");
            DateTime today = date.Date;
            MemoryCard previous;
            State.Cards.TryGetValue(id, out previous);
            var card = new MemoryCard
            {
                Reviews = previous == null ? 1 : previous.Reviews + 1,
                Recalled = (previous == null ? 0 : previous.Recalled) + (rating == RecallRating.Recalled ? 1 : 0),
                LastRating = (int)rating,
                FirstDay = previous == null ? null : previous.FirstDay,
                Step = previous == null ? 0 : previous.Step,
                DueDay = previous == null ? null : previous.DueDay
            };
            if (rating == RecallRating.Forgot)
            {
                card.FirstDay = null;
                card.Step = 0;
                card.DueDay = Day(today.AddDays(1));
            }
            else if (rating == RecallRating.Fuzzy)
            {
                card.DueDay = Day(today.AddDays(1));
                if (card.Step == ReviewDays.Length)
                {
                    card.FirstDay = null;
                    card.Step = 0;
                }
            }
            else if (card.FirstDay == null)
            {
                card.FirstDay = Day(today);
                card.Step = 0;
                card.DueDay = Day(today.AddDays(ReviewDays[0]));
            }
            else if (card.DueDay != null && string.CompareOrdinal(card.DueDay, Day(today)) <= 0)
            {
                card.Step++;
                if (card.Step >= ReviewDays.Length) card.DueDay = null;
                else
                {
                    DateTime anchor = DateTime.ParseExact(card.FirstDay, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    DateTime next = anchor.AddDays(ReviewDays[card.Step]);
                    card.DueDay = Day(next > today ? next : today.AddDays(1));
                }
            }

            State.Cards[id] = card;
            try { Save(); }
            catch
            {
                if (previous == null) State.Cards.Remove(id);
                else State.Cards[id] = previous;
                throw;
            }
            StudyRevision = Revision;
            return card;
        }

        internal void SaveNote(StudyNote note)
        {
            int index = State.Notes.FindIndex(n => n.Id == note.Id);
            if (index < 0)
            {
                State.Notes.Add(note);
                try { Save(); }
                catch { State.Notes.RemoveAt(State.Notes.Count - 1); throw; }
            }
            else
            {
                StudyNote previous = State.Notes[index];
                State.Notes[index] = note;
                try { Save(); }
                catch { State.Notes[index] = previous; throw; }
            }
        }

        internal void SavePalace(List<PalacePlace> places)
        {
            List<PalacePlace> old = State.Palace;
            State.Palace = places;
            try { Save(); }
            catch { State.Palace = old; throw; }
        }

        private static string Day(DateTime date)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
