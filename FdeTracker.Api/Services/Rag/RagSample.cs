namespace FdeTracker.Api.Services.Rag;

public sealed record SampleDoc(string Title, string[] Tags, string Body);
public sealed record EvalQuestion(string Question, string? Doc, string? Phrase);

/// <summary>A small approved document set (an invented company handbook) and a fixed evaluation set over it: 14 questions the documents answer, 2 they do not.</summary>
public static class RagSample
{
    public static readonly SampleDoc[] Docs =
    [
        new("Leave policy", ["hr"],
            "Every full-time employee gets 24 days of paid annual leave per calendar year. Leave accrues monthly at 2 days per month. Up to 10 unused days can be carried over to the next year; anything above that expires on 31 March.\n\n" +
            "Sick leave is separate from annual leave: 12 days per year. A medical certificate is needed when sick leave lasts more than 2 consecutive days. Tell your manager before 10:00 on the first day of absence.\n\n" +
            "Parental leave is 26 weeks for the primary caregiver and 4 weeks for the other parent. Requests must be sent to HR at least 8 weeks before the expected start date."),
        new("Expense policy", ["finance"],
            "Business expenses are reimbursed within 14 days of a complete claim. Submit claims in the finance portal with the original receipt attached. Claims without receipts are rejected.\n\n" +
            "Flights must be booked in economy class at least 14 days ahead. Hotel stays are capped at 120 euros per night in most cities and 180 euros in London and New York. Meals during travel are covered up to 45 euros per day.\n\n" +
            "Any single purchase above 500 euros needs written approval from a director before you buy. Software subscriptions are bought only by the IT team, never on a personal card."),
        new("Remote work and security", ["it"],
            "Employees may work remotely up to 3 days a week after completing their probation period of 3 months. Team days in the office are Tuesday and Thursday.\n\n" +
            "Company laptops must use full disk encryption and lock automatically after 5 minutes. Use the VPN on any public Wi-Fi. Never store customer data on personal devices or in personal cloud drives.\n\n" +
            "Report a lost device or a suspected phishing email to the security desk within 1 hour. Passwords must have at least 14 characters and multi-factor authentication is mandatory for all company accounts."),
        new("Onboarding and training", ["hr"],
            "New starters get a buddy for the first 8 weeks. The first week covers tools, security training and a tour of the main systems. A check-in with the manager happens at 30, 60 and 90 days.\n\n" +
            "Each employee has a learning budget of 1,500 euros per year for courses, books and conferences. Conference attendance needs manager approval at least 30 days before the event. The learning budget does not roll over.\n\n" +
            "Mentoring is open to everyone: ask the people team to match you with a mentor from another department. Mentoring pairs meet once every 2 weeks for 45 minutes."),
    ];

    public static readonly EvalQuestion[] Questions =
    [
        // Plain questions: they use the words of the documents.
        new("How many days of paid annual leave do I get?", "Leave policy", "24 days"),
        new("How many unused leave days can I carry over?", "Leave policy", "10 unused days"),
        new("When do I need a medical certificate for sick leave?", "Leave policy", "more than 2 consecutive days"),
        new("How soon are expense claims reimbursed?", "Expense policy", "within 14 days"),
        new("What is the hotel cap per night in London?", "Expense policy", "180 euros"),
        new("How many days a week can I work remotely?", "Remote work and security", "3 days a week"),
        new("After how many minutes does a company laptop lock itself?", "Remote work and security", "5 minutes"),
        new("How long must a password be?", "Remote work and security", "14 characters"),
        new("What is the yearly learning budget?", "Onboarding and training", "1,500 euros"),
        new("How often do mentoring pairs meet?", "Onboarding and training", "once every 2 weeks"),
        // Hard questions: other words for the same thing, or a number that also appears in another document.
        new("How many vacation days do staff receive each year?", "Leave policy", "24 days"),
        new("What happens to leftover holiday days at the end of the year?", "Leave policy", "10 unused days"),
        new("Can I work from home?", "Remote work and security", "3 days a week"),
        new("How many days ahead must flights be booked?", "Expense policy", "14 days ahead"),
        new("Who signs off on big purchases?", "Expense policy", "director"),
        new("Who do I tell when I am ill?", "Leave policy", "manager"),
        // Not in the documents: the assistant must say so.
        new("What is the company dress code?", null, null),
        new("How much is the annual bonus?", null, null),
    ];
}

public sealed record EvalRow(string Question, bool Answerable, bool Hit, int? Rank, bool AnswerOk, bool CitationOk, string Answer);
public sealed record EvalSummary(string Strategy, string Mode, int TopK, int Chunks, int Answerable, double HitRate, double Mrr, double AnswerRate, double CitationRate, double RefusalRate, List<EvalRow> Rows);

public static class RagEval
{
    public static List<Chunk> Corpus(string strategy, int? size = null, int? overlap = null) =>
        RagSample.Docs.SelectMany((d, i) => Chunker.Split(new Guid(i + 1, 0, 0, new byte[8]), d.Title, d.Tags, d.Body, strategy, size, overlap)).ToList();

    /// <summary>Runs the fixed question set with one setting. Retrieval: was a chunk of the right document in the top K (hit rate, and 1/rank of the first one). Answer: the extractive answer contains the expected phrase and cites the right document. Unanswerable questions must be refused.</summary>
    public static EvalSummary Run(string strategy, string mode, int topK, int? size = null, int? overlap = null)
    {
        var chunks = Corpus(strategy, size, overlap);
        var rows = new List<EvalRow>();
        foreach (var q in RagSample.Questions)
        {
            var hits = Retriever.Search(chunks, q.Question, mode, topK);
            var ans = Answerer.Extractive(q.Question, hits);
            if (q.Doc is null)
            {
                var refused = ans.Answer == Answerer.Refusal;
                rows.Add(new EvalRow(q.Question, false, refused, null, refused, refused, ans.Answer));
                continue;
            }
            var rank = hits.FindIndex(h => h.Chunk.Title == q.Doc) is var r && r >= 0 ? r + 1 : (int?)null;
            var ok = ans.Answer.Contains(q.Phrase!, StringComparison.OrdinalIgnoreCase);
            var cite = ans.Sources.Count > 0 && ans.Sources.All(s => s.Title == q.Doc);
            rows.Add(new EvalRow(q.Question, true, rank is not null, rank, ok, cite && ok, ans.Answer));
        }
        var a = rows.Where(r => r.Answerable).ToList();
        var u = rows.Where(r => !r.Answerable).ToList();
        double Pct(IEnumerable<bool> xs, int n) => n == 0 ? 0 : Math.Round(100.0 * xs.Count(x => x) / n, 1);
        return new EvalSummary(strategy, mode, topK, chunks.Count, a.Count, Pct(a.Select(r => r.Hit), a.Count),
            a.Count == 0 ? 0 : Math.Round(a.Average(r => r.Rank is { } k ? 1.0 / k : 0), 3), Pct(a.Select(r => r.AnswerOk), a.Count), Pct(a.Select(r => r.CitationOk), a.Count), Pct(u.Select(r => r.Hit), u.Count), rows);
    }

    public static List<EvalSummary> Compare() =>
        (from s in Chunker.Strategies from m in Retriever.Modes from k in new[] { 1, 3, 5 } select Run(s, m, k)).ToList();
}
