using System.Text.RegularExpressions;

namespace FdeTracker.Api.Services.Rag;

public sealed record Chunk(Guid DocId, string Title, int Index, string Text, string[] Tags);
public sealed record Hit(Chunk Chunk, double Score);

/// <summary>Two ways to cut a document: fixed windows with overlap, or by paragraph (long paragraphs are cut at sentence ends).</summary>
public static class Chunker
{
    public const string Fixed = "fixed", Paragraph = "paragraph";
    public static readonly string[] Strategies = [Fixed, Paragraph];
    public const int FixedSize = 260, FixedOverlap = 60, ParagraphMax = 420;

    /// <summary>Size is the window length (fixed) or the longest chunk (paragraph); overlap applies to fixed windows only.</summary>
    public static List<Chunk> Split(Guid docId, string title, string[] tags, string text, string strategy, int? size = null, int? overlap = null)
    {
        var pieces = strategy switch
        {
            Fixed => Windows(text, size ?? FixedSize, overlap ?? FixedOverlap),
            Paragraph => Paragraphs(text, size ?? ParagraphMax),
            _ => throw new ArgumentException("Unknown chunking strategy"),
        };
        return pieces.Select((p, i) => new Chunk(docId, title, i + 1, p, tags)).ToList();
    }

    static List<string> Windows(string text, int size, int overlap)
    {
        var t = Regex.Replace(text.Trim(), @"\s+", " ");
        var list = new List<string>();
        var start = 0;
        while (start < t.Length)
        {
            var end = Math.Min(t.Length, start + size);
            if (end < t.Length) { var sp = t.LastIndexOf(' ', end - 1, end - start - size / 2); if (sp > start) end = sp; }
            var piece = t[start..end].Trim();
            if (piece.Length > 0) list.Add(piece);
            if (end >= t.Length) break;
            var next = Math.Max(end - overlap, start + 1);
            var sp2 = t.IndexOf(' ', next);                 // begin the next window at a word start
            start = sp2 < 0 || sp2 >= end ? end : sp2 + 1;
        }
        return list;
    }

    static List<string> Paragraphs(string text, int max)
    {
        var list = new List<string>();
        foreach (var para in Regex.Split(text.Trim(), @"\r?\n\s*\r?\n").Select(p => Regex.Replace(p, @"\s+", " ").Trim()).Where(p => p.Length > 0))
        {
            if (para.Length <= max) { list.Add(para); continue; }
            var cur = "";
            foreach (var s in Regex.Split(para, @"(?<=[.!?])\s+"))
            {
                if (cur.Length > 0 && cur.Length + s.Length + 1 > max) { list.Add(cur); cur = ""; }
                cur = cur.Length == 0 ? s : cur + " " + s;
            }
            if (cur.Length > 0) list.Add(cur);
        }
        return list;
    }
}

/// <summary>Words reduced to a rough stem, without the very common ones. Keyword and vector search both use it, so they see the same words.</summary>
public static class Words
{
    static readonly HashSet<string> Stop = ("a an and are as at be by do does for from how i if in is it me my of on or so than that the then there these this to us was we what when where which who why will with you your can get has have had many much long").Split(' ').ToHashSet();

    public static string Stem(string w)
    {
        if (w.EndsWith("ies") && w.Length > 4) w = w[..^3] + "y";
        else if (w.EndsWith('s') && !w.EndsWith("ss") && w.Length > 3) w = w[..^1];
        if (w.EndsWith("ing") && w.Length > 5) w = w[..^3];
        else if (w.EndsWith("ed") && w.Length > 4) w = w[..^2];
        return w;
    }

    public static List<string> Tokens(string text) =>
        Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).Where(w => !Stop.Contains(w)).Select(Stem).ToList();
}

/// <summary>
/// A local embedding without a model: words, word pairs and word pieces are hashed into 256 numbers (feature hashing) and scaled to length 1.
/// Texts that share words and phrases get a high cosine. It does NOT know that two different words mean the same thing: a trained embedding model does.
/// </summary>
public static class HashEmbedder
{
    public const int Dim = 256;

    // Embeddings are generated once per distinct text and kept (in memory, bounded), so a question never re-embeds a chunk it has seen.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float[]> Store = new();
    const int MaxStored = 20000;
    public static int Stored => Store.Count;

    public static float[] EmbedStored(string text)
    {
        if (Store.Count > MaxStored) Store.Clear();
        return Store.GetOrAdd(text, Embed);
    }

    public static float[] Embed(string text)
    {
        var v = new float[Dim];
        var t = Words.Tokens(text);
        void Add(string feature, float weight) { var h = Fnv(feature); v[(int)(h % Dim)] += ((h >> 8) & 1) == 0 ? weight : -weight; }
        for (var i = 0; i < t.Count; i++)
        {
            Add("w:" + t[i], 1f);
            if (i + 1 < t.Count) Add("b:" + t[i] + " " + t[i + 1], 0.5f);
            if (t[i].Length >= 5) for (var k = 0; k + 3 <= t[i].Length; k++) Add("c:" + t[i].Substring(k, 3), 0.25f);
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < Dim; i++) v[i] /= norm;
        return v;
    }

    public static double Cosine(float[] a, float[] b) { double d = 0; for (var i = 0; i < a.Length; i++) d += a[i] * b[i]; return d; }

    static uint Fnv(string s) { var h = 2166136261u; foreach (var c in s) { h ^= c; h *= 16777619u; } return h; }
}

public static class Retriever
{
    public const string Semantic = "semantic", Keyword = "keyword", Hybrid = "hybrid";
    public static readonly string[] Modes = [Semantic, Keyword, Hybrid];

    /// <summary>Top-K chunks for a question. A tag filters by document metadata before ranking. Hybrid merges the two rankings (reciprocal rank fusion).</summary>
    public static List<Hit> Search(IReadOnlyList<Chunk> all, string query, string mode, int topK, string? tag = null)
    {
        var pool = string.IsNullOrWhiteSpace(tag) ? all : all.Where(c => c.Tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0 || topK < 1) return [];
        var qv = HashEmbedder.Embed(query);
        var sem = pool.Select(c => HashEmbedder.Cosine(HashEmbedder.EmbedStored(c.Text), qv)).ToArray();
        var kw = Bm25(pool, query);
        double[] score = mode switch
        {
            Semantic => sem,
            Keyword => kw,
            Hybrid => Fuse(sem, kw),
            _ => throw new ArgumentException("Unknown search mode"),
        };
        return Enumerable.Range(0, pool.Count).Where(i => score[i] > 1e-9).OrderByDescending(i => score[i]).ThenBy(i => i).Take(topK)
            .Select(i => new Hit(pool[i], Math.Round(score[i], 4))).ToList();
    }

    static double[] Fuse(double[] a, double[] b)
    {
        double[] Rr(double[] s)
        {
            var r = new double[s.Length];
            var ord = Enumerable.Range(0, s.Length).OrderByDescending(i => s[i]).ThenBy(i => i).ToArray();
            for (var k = 0; k < ord.Length; k++) r[ord[k]] = s[ord[k]] > 1e-9 ? 1.0 / (60 + k + 1) : 0;
            return r;
        }
        var ra = Rr(a); var rb = Rr(b);
        return ra.Zip(rb, (x, y) => x + y).ToArray();
    }

    static double[] Bm25(IReadOnlyList<Chunk> pool, string query)
    {
        const double k1 = 1.5, b = 0.75;
        var docs = pool.Select(c => Words.Tokens(c.Text)).ToList();
        var avg = docs.Average(d => (double)d.Count); if (avg == 0) avg = 1;
        var scores = new double[pool.Count];
        foreach (var term in Words.Tokens(query).Distinct())
        {
            var n = docs.Count(d => d.Contains(term));
            if (n == 0) continue;
            var idf = Math.Log(1 + (docs.Count - n + 0.5) / (n + 0.5));
            for (var i = 0; i < docs.Count; i++)
            {
                var tf = docs[i].Count(w => w == term);
                if (tf == 0) continue;
                scores[i] += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * docs[i].Count / avg));
            }
        }
        return scores;
    }
}

public sealed record Source(int N, string Title, int Chunk, string Text, double Score);
public sealed record RagAnswer(string Answer, bool Grounded, string By, List<Source> Sources);

/// <summary>The answer step. Extractive: the best sentences from the retrieved chunks, quoted with their source number. It refuses when the documents do not cover the question.</summary>
public static class Answerer
{
    public const string Refusal = "I could not find this in the approved documents.";
    static readonly Regex Cite = new(@"\[(\d{1,2})\]", RegexOptions.Compiled);

    public static List<Source> ToSources(IReadOnlyList<Hit> hits) => hits.Select((h, i) => new Source(i + 1, h.Chunk.Title, h.Chunk.Index, h.Chunk.Text, h.Score)).ToList();

    public static RagAnswer Extractive(string query, IReadOnlyList<Hit> hits)
    {
        var sources = ToSources(hits);
        var q = Words.Tokens(query).Distinct().ToList();
        var scored = sources.SelectMany(s => Regex.Split(s.Text, @"(?<=[.!?])\s+").Select(sent =>
        {
            var tokens = Words.Tokens(sent).ToHashSet();
            var matched = q.Count(tokens.Contains);
            return (Sent: sent.Trim(), s.N, Matched: matched, Ratio: q.Count == 0 ? 0 : (double)matched / q.Count);
        })).Where(x => x.Matched >= 2 && x.Ratio >= 0.5).OrderByDescending(x => x.Ratio).ThenByDescending(x => x.Matched).ThenBy(x => x.N).Take(2).ToList();
        if (scored.Count == 0) return new RagAnswer(Refusal, true, "extractive", []);
        var used = scored.Select(x => x.N).Distinct().ToList();
        return new RagAnswer(string.Join(" ", scored.Select(x => $"{x.Sent} [{x.N}]")), true, "extractive", sources.Where(s => used.Contains(s.N)).ToList());
    }

    /// <summary>Checks a model's answer: every [n] must point at a real source; an answer without one is not "grounded".</summary>
    public static RagAnswer CheckModel(string text, IReadOnlyList<Hit> hits)
    {
        var sources = ToSources(hits);
        var t = text.Trim();
        if (t.StartsWith(Refusal[..20], StringComparison.OrdinalIgnoreCase)) return new RagAnswer(Refusal, true, "model", []);
        var cited = Cite.Matches(t).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
        var valid = cited.Where(n => n >= 1 && n <= sources.Count).ToList();
        t = Cite.Replace(t, m => int.Parse(m.Groups[1].Value) is var n && n >= 1 && n <= sources.Count ? m.Value : "");
        return new RagAnswer(t.Trim(), valid.Count > 0 && valid.Count == cited.Count, "model", sources.Where(s => valid.Contains(s.N)).ToList());
    }
}
