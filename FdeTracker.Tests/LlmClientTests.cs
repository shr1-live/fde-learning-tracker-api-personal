using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FdeTracker.Api.Common;
using FdeTracker.Api.Models.Requests;
using FdeTracker.Api.Services.Llm;
using Microsoft.Extensions.Configuration;

namespace FdeTracker.Tests;

/// <summary>Provider clients tested with a fake HTTP layer: no network, no key, no cost.</summary>
public sealed class LlmClientTests
{
    sealed class Stub(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; } // like a real handler, tests can honour it
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Token = ct;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return await reply(request, body);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static AppOptions Opts(string provider = "anthropic", string? baseUrl = null, int timeout = 5) =>
        new() { LlmProvider = provider, LlmApiKey = "test-key", LlmModel = "test-model", LlmBaseUrl = baseUrl, LlmTimeoutSeconds = timeout, LlmRetryDelayMs = 1 };

    const string ClaudeAnswer = """{"model":"claude-test","content":[{"type":"text","text":"{\"ok\":true}"}],"usage":{"input_tokens":11,"output_tokens":7}}""";

    [Fact]
    public async Task Claude_request_has_the_right_shape_and_the_answer_is_read()
    {
        HttpRequestMessage? seen = null; string sentBody = "";
        var stub = new Stub((req, body) => { seen = req; sentBody = body; return Task.FromResult(Json(HttpStatusCode.OK, ClaudeAnswer)); });
        var client = LlmClientFactory.Create("anthropic", new HttpClient(stub), Opts());

        var r = await client.CompleteAsync("SYSTEM TEXT", "USER TEXT", CancellationToken.None);

        Assert.Equal("https://api.anthropic.com/v1/messages", seen!.RequestUri!.ToString());
        Assert.Equal("test-key", seen.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", seen.Headers.GetValues("anthropic-version").Single());
        var sent = JsonNode.Parse(sentBody)!;
        Assert.Equal("test-model", (string)sent["model"]!);
        Assert.Equal("SYSTEM TEXT", (string)sent["system"]!);
        Assert.Equal("USER TEXT", (string)sent["messages"]![0]!["content"]!);
        Assert.True((int)sent["max_tokens"]! > 0);
        Assert.Equal("""{"ok":true}""", r.Text);
        Assert.Equal((11, 7, "claude-test"), (r.InputTokens, r.OutputTokens, r.Model));
    }

    [Fact]
    public async Task Claude_overload_is_retried_and_then_succeeds()
    {
        var stub = new Stub((_, _) => Task.FromResult(Json(stubCalls++ == 0 ? (HttpStatusCode)529 : HttpStatusCode.OK, ClaudeAnswer)));
        var client = LlmClientFactory.Create("anthropic", new HttpClient(stub), Opts());
        var r = await client.CompleteAsync("s", "u", CancellationToken.None);
        Assert.Equal(2, stub.Calls);
        Assert.Equal(11, r.InputTokens);
    }
    int stubCalls;

    [Fact]
    public async Task A_provider_error_never_leaks_the_provider_body_or_the_key()
    {
        var stub = new Stub((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"error":"secret-detail test-key"}""")));
        var client = LlmClientFactory.Create("anthropic", new HttpClient(stub), Opts());
        var e = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Equal("provider_error", e.Kind);
        Assert.DoesNotContain("secret-detail", e.Message);
        Assert.DoesNotContain("test-key", e.Message);
        Assert.Equal(1, stub.Calls); // a 401 is not retried
    }

    [Fact]
    public async Task A_slow_provider_becomes_a_timeout()
    {
        Stub? stub = null;
        stub = new Stub(async (_, _) => { await Task.Delay(TimeSpan.FromSeconds(10), stub!.Token); return Json(HttpStatusCode.OK, ClaudeAnswer); });
        var client = LlmClientFactory.Create("anthropic", new HttpClient(stub) { Timeout = Timeout.InfiniteTimeSpan }, Opts(timeout: 1));
        var e = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Equal("timeout", e.Kind);
    }

    [Fact]
    public async Task A_garbled_answer_is_a_clean_error()
    {
        var stub = new Stub((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"unexpected":true}""")));
        var client = LlmClientFactory.Create("anthropic", new HttpClient(stub), Opts());
        var e = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Equal("provider_error", e.Kind);
    }

    [Fact]
    public async Task The_chat_completions_client_still_sends_a_bearer_token_and_reads_usage()
    {
        HttpRequestMessage? seen = null;
        var stub = new Stub((req, _) => { seen = req; return Task.FromResult(Json(HttpStatusCode.OK, """{"model":"m1","choices":[{"message":{"content":"{}"}}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""")); });
        var client = LlmClientFactory.Create("openai", new HttpClient(stub), Opts("openai", "http://host/v1"));
        var r = await client.CompleteAsync("s", "u", CancellationToken.None);
        Assert.Equal("http://host/v1/chat/completions", seen!.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", seen.Headers.Authorization!.ToString());
        Assert.Equal((5, 3), (r.InputTokens, r.OutputTokens));
    }

    [Fact]
    public void Configured_means_the_right_settings_for_each_provider()
    {
        var http = new HttpClient(new Stub((_, _) => throw new InvalidOperationException()));
        Assert.True(LlmClientFactory.Create("anthropic", http, Opts("anthropic")).IsConfigured); // address is optional for Claude
        Assert.False(LlmClientFactory.Create("openai", http, Opts("openai")).IsConfigured);      // the chat API needs an address
        Assert.True(LlmClientFactory.Create("openai", http, Opts("openai", "http://x/v1")).IsConfigured);
        Assert.Throws<InvalidOperationException>(() => LlmClientFactory.Create("nope", http, Opts()));
    }

    [Fact]
    public void An_unknown_provider_setting_is_refused_at_start_up()
    {
        IConfiguration Cfg(string? provider) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DATABASE_URL"] = "x", ["SUPABASE_URL"] = "https://x.supabase.co", ["LLM_PROVIDER"] = provider }).Build();
        Assert.Equal("anthropic", AppOptions.From(Cfg("Anthropic")).LlmProvider);
        Assert.Equal("openai", AppOptions.From(Cfg(null)).LlmProvider);
        Assert.Throws<InvalidOperationException>(() => AppOptions.From(Cfg("bogus")));
    }

    [Fact]
    public void The_parser_copes_with_words_and_code_fences_around_the_json()
    {
        var people = new List<CandidateInput> { new() { Name = "Asha", Profile = "Built REST APIs in Python for three years and used Docker." } };
        const string json = """{"candidates":[{"id":1,"summary":"ok","fit":"strong","matched_skills":[{"skill":"Python","evidence":"Built REST APIs in Python"}],"gaps":[],"not_stated":[]}]}""";
        foreach (var wrapped in new[] { json, "Here is the analysis:\n" + json + "\nHope that helps!", "```json\n" + json + "\n```" })
        {
            var (c, _) = AnalysisParser.Parse(wrapped, people, ["Python"]);
            Assert.Single(c[0].MatchedSkills);
        }
    }

    [Fact]
    public async Task A_claude_answer_cut_off_at_the_length_limit_is_an_error_not_a_success()
    {
        var stub = new Stub((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"content":[{"type":"text","text":"{\"half"}],"stop_reason":"max_tokens","usage":{"input_tokens":1,"output_tokens":1500}}""")));
        var client = LlmClientFactory.Create("anthropic", new HttpClient(stub), Opts());
        var e = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
        Assert.Contains("cut off", e.Message);
    }

    [Fact]
    public async Task An_empty_claude_answer_is_an_error()
    {
        var stub = new Stub((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"content":[],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":0}}""")));
        var client = LlmClientFactory.Create("anthropic", new HttpClient(stub), Opts());
        await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync("s", "u", CancellationToken.None));
    }

    [Fact]
    public void A_leftover_base_address_cannot_receive_a_claude_key()
    {
        IConfiguration Cfg(string provider, string? baseUrl) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DATABASE_URL"] = "x", ["SUPABASE_URL"] = "https://x.supabase.co", ["LLM_PROVIDER"] = provider, ["LLM_BASE_URL"] = baseUrl }).Build();
        Assert.Throws<InvalidOperationException>(() => AppOptions.From(Cfg("anthropic", "https://generativelanguage.googleapis.com")));
        Assert.Equal("anthropic", AppOptions.From(Cfg("anthropic", null)).LlmProvider);
        Assert.Equal("https://example/v1", AppOptions.From(Cfg("openai", "https://example/v1")).LlmBaseUrl);
    }

    [Fact]
    public void The_parser_survives_braces_in_the_surrounding_words()
    {
        var people = new List<CandidateInput> { new() { Name = "Asha", Profile = "Built REST APIs in Python for three years and used Docker." } };
        const string json = """{"candidates":[{"id":1,"summary":"ok","fit":"strong","matched_skills":[{"skill":"Python","evidence":"Built REST APIs in Python"}],"gaps":[],"not_stated":[]}]}""";
        var (c, _) = AnalysisParser.Parse("Here is the {analysis}: " + json + " (see {above})", people, ["Python"]);
        Assert.Single(c[0].MatchedSkills);
    }
}
