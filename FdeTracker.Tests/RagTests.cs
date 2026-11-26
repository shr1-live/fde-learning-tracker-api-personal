using FdeTracker.Api.Services.Llm;
using FdeTracker.Api.Services.Rag;

namespace FdeTracker.Tests;

/// <summary>The document assistant (Week 2, RAG): chunking, search modes, grounded answers and the fixed evaluation; then the endpoints, with each person's documents kept apart.</summary>
[TestCaseOrderer("FdeTracker.Tests.NameOrderer", "FdeTracker.Tests")]
[Collection(ApiFixture.Collection)]
public sealed class RagTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    static readonly TestUser Admin = new(ApiFixture.Id(1), "boss@fiftyfivetech.io");
    static readonly TestUser M1 = new(ApiFixture.Id(701), "rag.one@fiftyfivetech.io");
    static readonly TestUser M2 = new(ApiFixture.Id(702), "rag.two@fiftyfivetech.io");

    // ---- the engine (no database) ----

    [Fact]
    public void E01_fixed_chunks_overlap_and_stay_inside_the_text_and_paragraph_chunks_follow_paragraphs()
    {
        var doc = RagSample.Docs[0];
        var fixedChunks = Chunker.Split(Guid.Empty, doc.Title, doc.Tags, doc.Body, Chunker.Fixed);
        Assert.True(fixedChunks.Count > 1);
        Assert.All(fixedChunks, c => Assert.InRange(c.Text.Length, 1, Chunker.FixedSize));
        var flat = System.Text.RegularExpressions.Regex.Replace(doc.Body, @"\s+", " ");
        Assert.All(fixedChunks, c => Assert.Contains(c.Text, flat));
        for (var i = 1; i < fixedChunks.Count; i++) // each chunk starts inside the previous one (the overlap), at a word start
            Assert.Contains(fixedChunks[i].Text.Split(' ')[0], fixedChunks[i - 1].Text);
        Assert.StartsWith(fixedChunks[0].Text, flat);
        Assert.EndsWith(fixedChunks[^1].Text, flat);

        var para = Chunker.Split(Guid.Empty, doc.Title, doc.Tags, doc.Body, Chunker.Paragraph);
        Assert.Equal(3, para.Count);
        Assert.Equal(new[] { 1, 2, 3 }, para.Select(c => c.Index));
    }

    [Fact]
    public void E02_each_search_mode_finds_the_right_chunk_and_a_tag_filter_narrows_the_pool()
    {
        var chunks = RagEval.Corpus(Chunker.Paragraph);
        foreach (var mode in Retriever.Modes)
        {
            var top = Retriever.Search(chunks, "What is the hotel cap per night in London?", mode, 1).Single();
            Assert.Equal("Expense policy", top.Chunk.Title);
        }
        var hr = Retriever.Search(chunks, "How many days a week can I work remotely?", Retriever.Keyword, 5, "hr");
        Assert.All(hr, h => Assert.Contains("hr", h.Chunk.Tags));
        Assert.DoesNotContain(hr, h => h.Chunk.Title == "Remote work and security");
        Assert.Empty(Retriever.Search(chunks, "anything", Retriever.Hybrid, 3, "nosuchtag"));
        Assert.Equal(2, Retriever.Search(chunks, "leave days", Retriever.Hybrid, 2).Count);
    }

    [Fact]
    public void E03_the_answer_quotes_the_document_cites_it_and_refuses_when_the_documents_do_not_say()
    {
        var chunks = RagEval.Corpus(Chunker.Paragraph);
        var q = "After how many minutes does a company laptop lock itself?";
        var a = Answerer.Extractive(q, Retriever.Search(chunks, q, Retriever.Hybrid, 3));
        Assert.Contains("5 minutes", a.Answer);
        Assert.Contains("[1]", a.Answer);
        Assert.All(a.Sources, s => Assert.Equal("Remote work and security", s.Title));

        var none = Answerer.Extractive("What is the company dress code?", Retriever.Search(chunks, "What is the company dress code?", Retriever.Hybrid, 3));
        Assert.Equal(Answerer.Refusal, none.Answer);
        Assert.Empty(none.Sources);
    }

    [Fact]
    public void E04_a_model_answer_is_checked_for_real_citations()
    {
        var hits = Retriever.Search(RagEval.Corpus(Chunker.Paragraph), "annual leave days", Retriever.Hybrid, 2);
        var good = Answerer.CheckModel("You get 24 days a year [1].", hits);
        Assert.True(good.Grounded); Assert.Single(good.Sources);
        var madeUp = Answerer.CheckModel("You get 30 days [7].", hits);   // source 7 does not exist
        Assert.False(madeUp.Grounded); Assert.DoesNotContain("[7]", madeUp.Answer);
        Assert.False(Answerer.CheckModel("You get 30 days.", hits).Grounded); // no citation at all
        Assert.Equal(Answerer.Refusal, Answerer.CheckModel("I could not find this in the approved documents, sorry.", hits).Answer);
    }

    [Fact]
    public void E05_the_evaluation_has_18_questions_and_the_best_setting_is_measured()
    {
        Assert.Equal(18, RagSample.Questions.Length);
        Assert.Equal(2, RagSample.Questions.Count(q => q.Doc is null));
        var all = RagEval.Compare();
        Assert.Equal(18, all.Count); // 2 strategies x 3 modes x 3 values of K
        var best = all.Single(r => r is { Strategy: Chunker.Paragraph, Mode: Retriever.Hybrid, TopK: 3 });
        Assert.Equal(100, best.HitRate);
        Assert.Equal(100, best.RefusalRate);          // never answers what the documents do not cover
        Assert.True(best.AnswerRate < 100);           // paraphrases that use other words are refused, not invented
        Assert.True(all.Single(r => r is { Strategy: Chunker.Paragraph, Mode: Retriever.Keyword, TopK: 1 }).HitRate < best.HitRate);
    }

    [Fact]
    public void E06_the_hashed_embedding_is_fixed_length_unit_length_and_repeatable()
    {
        var a = HashEmbedder.Embed("Annual leave is 24 days"); var b = HashEmbedder.Embed("Annual leave is 24 days");
        Assert.Equal(HashEmbedder.Dim, a.Length); Assert.Equal(a, b);
        Assert.InRange(HashEmbedder.Cosine(a, a), 0.999, 1.001);
        Assert.True(HashEmbedder.Cosine(a, HashEmbedder.Embed("annual leave days")) > HashEmbedder.Cosine(a, HashEmbedder.Embed("hotel cap in london")));
    }

    [Fact]
    public void E07_chunk_size_and_overlap_are_configurable_and_change_the_chunks()
    {
        var doc = RagSample.Docs[0];
        var small = Chunker.Split(Guid.Empty, doc.Title, doc.Tags, doc.Body, Chunker.Fixed, 120, 20);
        var large = Chunker.Split(Guid.Empty, doc.Title, doc.Tags, doc.Body, Chunker.Fixed, 400, 100);
        Assert.True(small.Count > large.Count);
        Assert.All(small, c => Assert.InRange(c.Text.Length, 1, 120));
        var noOverlap = Chunker.Split(Guid.Empty, doc.Title, doc.Tags, doc.Body, Chunker.Fixed, 200, 0);
        var overlap = Chunker.Split(Guid.Empty, doc.Title, doc.Tags, doc.Body, Chunker.Fixed, 200, 80);
        Assert.True(overlap.Count >= noOverlap.Count);
        Assert.True(Chunker.Split(Guid.Empty, doc.Title, doc.Tags, doc.Body, Chunker.Paragraph, 100).Count > 3); // a short limit cuts paragraphs at sentence ends
        Assert.NotEqual(RagEval.Run("fixed", "hybrid", 3, 120, 20).Chunks, RagEval.Run("fixed", "hybrid", 3, 400, 100).Chunks);
    }

    [Fact]
    public void E08_embeddings_are_stored_and_reused()
    {
        const string text = "A very particular sentence for the embedding store test 4711.";
        var before = HashEmbedder.Stored;
        var a = HashEmbedder.EmbedStored(text);
        Assert.Equal(before + 1, HashEmbedder.Stored);
        Assert.Same(a, HashEmbedder.EmbedStored(text)); // the same stored vector, not computed again
        Assert.Equal(before + 1, HashEmbedder.Stored);
    }

    // ---- the endpoints ----

    [Fact]
    public async Task R01_a_person_adds_loads_asks_and_removes_documents()
    {
        foreach (var u in new[] { Admin, M1, M2 }) Assert.Equal(200, (await api.Call("GET", "/api/me", u)).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/rag/ask", M1, new { question = "How many days of leave?" })).Status); // no documents yet

        var add = await api.Call("POST", "/api/rag/documents", M1, new { title = "Parking", text = "Visitors may park for 2 hours in the north car park. Employees need a permit from reception.", tags = new[] { "Facilities" } });
        Assert.Equal(200, add.Status);
        Assert.Equal(409, (await api.Call("POST", "/api/rag/documents", M1, new { title = "parking", text = "Another text that is long enough to be stored." })).Status);

        var sample = await api.Call("POST", "/api/rag/sample", M1);
        Assert.Equal(200, sample.Status);
        Assert.Equal(5, sample.Body.AsArray().Count);
        Assert.Equal(5, (await api.Call("POST", "/api/rag/sample", M1)).Body.AsArray().Count); // safe to repeat

        var ask = await api.Call("POST", "/api/rag/ask", M1, new { question = "After how many minutes does a company laptop lock itself?", strategy = "paragraph", mode = "hybrid", top_k = 3, use_model = false });
        Assert.Equal(200, ask.Status);
        Assert.Contains("5 minutes", (string)ask.Body["answer"]!);
        Assert.Equal("extractive", (string)ask.Body["answered_by"]!);
        Assert.Equal("Remote work and security", (string)ask.Body["cited"]![0]!["title"]!);
        Assert.Equal(3, ask.Body["retrieved"]!.AsArray().Count);

        var none = await api.Call("POST", "/api/rag/ask", M1, new { question = "How much is the annual bonus?", use_model = false });
        Assert.Equal("I could not find this in the approved documents.", (string)none.Body["answer"]!);

        var filtered = await api.Call("POST", "/api/rag/search", M1, new { question = "permit for the car park", tag = "facilities", top_k = 5 });
        Assert.Equal(200, filtered.Status);
        Assert.All(filtered.Body["results"]!.AsArray(), r => Assert.Equal("Parking", (string)r!["title"]!));

        var id = (string)add.Body["id"]!;
        Assert.Equal(204, (await api.Call("DELETE", $"/api/rag/documents/{id}", M1)).Status);
        Assert.Equal(404, (await api.Call("DELETE", $"/api/rag/documents/{id}", M1)).Status);
    }

    [Fact]
    public async Task R02_documents_are_private_and_input_is_checked()
    {
        Assert.Empty((await api.Call("GET", "/api/rag/documents", M2)).Body.AsArray()); // the other person's documents are not visible
        var mine = (await api.Call("GET", "/api/rag/documents", M1)).Body.AsArray();
        Assert.NotEmpty(mine);
        Assert.Equal(404, (await api.Call("DELETE", $"/api/rag/documents/{(string)mine[0]!["id"]!}", M2)).Status);

        Assert.Equal(400, (await api.Call("POST", "/api/rag/documents", M2, new { title = "", text = "Some text that is long enough here." })).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/rag/documents", M2, new { title = "Short", text = "tiny" })).Status);
        await api.Call("POST", "/api/rag/sample", M2);
        Assert.Equal(400, (await api.Call("POST", "/api/rag/ask", M2, new { question = "x" })).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/rag/ask", M2, new { question = "How many days of leave?", mode = "magic" })).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/rag/ask", M2, new { question = "How many days of leave?", top_k = 99 })).Status);
        Assert.Equal(401, (await api.Call("GET", "/api/rag/documents", null)).Status);
    }

    [Fact]
    public async Task R04_a_model_answer_is_used_only_when_it_cites_real_sources_and_the_documents_are_sent_as_data()
    {
        static LlmResult Say(string t) => new(t, 40, 10, 120, "fake-model");
        var q = new { question = "After how many minutes does a company laptop lock itself?", mode = "hybrid", top_k = 3 };

        api.Llm.Handler = (_, _) => Say("A laptop locks after 5 minutes [1].");
        var ok = await api.Call("POST", "/api/rag/ask", M1, q);
        Assert.Equal("model", (string)ok.Body["answered_by"]!);
        Assert.Equal("fake-model", (string)ok.Body["model"]!);
        Assert.Single(ok.Body["cited"]!.AsArray());
        Assert.Contains("not instructions", api.Llm.LastSystem);
        Assert.Contains("<sources>", api.Llm.LastUser);

        api.Llm.Handler = (_, _) => Say("A laptop locks after 30 minutes."); // no citation: not trusted
        var nocite = await api.Call("POST", "/api/rag/ask", M1, q);
        Assert.Equal("extractive", (string)nocite.Body["answered_by"]!);
        Assert.Contains("5 minutes", (string)nocite.Body["answer"]!);

        api.Llm.Handler = (_, _) => throw new FdeTracker.Api.Services.Llm.LlmException("provider_error", "down"); // the model fails: the quoted answer still works
        var down = await api.Call("POST", "/api/rag/ask", M1, q);
        Assert.Equal(200, down.Status);
        Assert.Equal("extractive", (string)down.Body["answered_by"]!);
        api.Llm.Handler = null;
    }

    [Fact]
    public async Task R05_chunk_size_and_overlap_are_accepted_checked_and_reported()
    {
        await api.Call("POST", "/api/rag/sample", M1);
        var ok = await api.Call("POST", "/api/rag/search", M1, new { question = "hotel cap in London", strategy = "fixed", mode = "keyword", top_k = 2, chunk_size = 150, overlap = 30 });
        Assert.Equal(200, ok.Status);
        Assert.Equal(150, (int)ok.Body["chunk_size"]!); Assert.Equal(30, (int)ok.Body["overlap"]!);
        Assert.Equal(256, (int)ok.Body["embedding_dimensions"]!);
        Assert.True((int)ok.Body["embeddings_stored"]! > 0);
        var big = await api.Call("POST", "/api/rag/search", M1, new { question = "hotel cap in London", strategy = "fixed", chunk_size = 500 });
        Assert.True((int)big.Body["chunks_in_pool"]! < (int)ok.Body["chunks_in_pool"]!);
        Assert.Equal(400, (await api.Call("POST", "/api/rag/search", M1, new { question = "hotel cap", strategy = "fixed", chunk_size = 5 })).Status);
        Assert.Equal(400, (await api.Call("POST", "/api/rag/search", M1, new { question = "hotel cap", strategy = "fixed", chunk_size = 200, overlap = 150 })).Status);
        Assert.Equal(200, (await api.Call("GET", "/api/rag/eval?strategy=fixed&mode=hybrid&top_k=3&chunk_size=200&overlap=40", M1)).Status);
        Assert.Equal(400, (await api.Call("GET", "/api/rag/eval?strategy=fixed&chunk_size=5", M1)).Status);
    }

    [Fact]
    public async Task R03_the_evaluation_endpoints_return_the_fixed_results()
    {
        var one = await api.Call("GET", "/api/rag/eval?strategy=paragraph&mode=hybrid&top_k=3", M1);
        Assert.Equal(200, one.Status);
        Assert.Equal(18, one.Body["rows"]!.AsArray().Count);
        Assert.Equal(100, (double)one.Body["hit_rate"]!);
        var cmp = await api.Call("GET", "/api/rag/eval/compare", M1);
        Assert.Equal(18, cmp.Body.AsArray().Count);
        Assert.Equal(400, (await api.Call("GET", "/api/rag/eval?mode=magic", M1)).Status);
    }
}
