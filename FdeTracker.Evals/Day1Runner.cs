using System.Text.Json;
using System.Text.Json.Serialization;
using FdeTracker.Api.Common;
using FdeTracker.Api.Services.Llm;

namespace FdeTracker.Evals;

public sealed class PromptCase
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("expect_any")] public string[]? ExpectAny { get; set; }
    [JsonPropertyName("expect_all")] public string[]? ExpectAll { get; set; }
    [JsonPropertyName("expect_none")] public string[]? ExpectNone { get; set; }
    [JsonPropertyName("expect_regex")] public string? ExpectRegex { get; set; }
    [JsonPropertyName("max_words")] public int? MaxWords { get; set; }
    [JsonPropertyName("min_question_marks")] public int? MinQuestionMarks { get; set; }
}

public sealed class PromptSet
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("system")] public string System { get; set; } = "";
    [JsonPropertyName("cases")] public List<PromptCase> Cases { get; set; } = [];
}

public sealed record CaseResult(string Model, string CaseId, string Kind, bool Passed, string Answer, int LatencyMs, int InputTokens, int OutputTokens, string? Error);

/// <summary>Day 1: run one fixed prompt set against several models and score each answer automatically.</summary>
public static class Day1Runner
{
    public static async Task<List<CaseResult>> Run(PromptSet set, IReadOnlyList<ResolvedTarget> targets, int timeoutSeconds)
    {
        var results = new List<CaseResult>();
        using var http = new HttpClient(new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var target in targets)
        {
            var model = target.Name; // results are labelled with the target name
            var client = LlmClientFactory.Create(target.Provider, http, new AppOptions { LlmProvider = target.Provider, LlmBaseUrl = string.IsNullOrWhiteSpace(target.BaseUrl) ? null : target.BaseUrl, LlmApiKey = target.ApiKey, LlmModel = target.Model, LlmTimeoutSeconds = timeoutSeconds });
            foreach (var c in set.Cases)
            {
                Console.Write($"{model,-14} {c.Id,-20} ");
                try
                {
                    var r = await client.CompleteAsync(set.System, c.Prompt, CancellationToken.None);
                    var answer = ExtractAnswer(r.Text);
                    var ok = Score(c, answer);
                    results.Add(new CaseResult(model, c.Id, c.Kind, ok, answer, r.LatencyMs, r.InputTokens, r.OutputTokens, null));
                    Console.WriteLine($"{(ok ? "pass" : "FAIL")}  {r.LatencyMs} ms");
                }
                catch (Exception e) when (e is LlmException or HttpRequestException or TaskCanceledException)
                {
                    var kind = e is LlmException le ? le.Kind : e.GetType().Name;
                    results.Add(new CaseResult(model, c.Id, c.Kind, false, "", 0, 0, 0, $"{kind}: {e.Message}")); // keep going: one failure must not lose the rest
                    Console.WriteLine($"ERROR {kind}");
                }
            }
        }
        return results;
    }

    static string ExtractAnswer(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("answer", out var a))
                return a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.ToString();
        }
        catch (JsonException) { /* fall through: keep the raw text */ }
        return text;
    }

    /// <summary>Each case sets its own simple, repeatable checks.</summary>
    public static bool Score(PromptCase c, string answer)
    {
        var lower = answer.ToLowerInvariant();
        if (c.ExpectAny is { Length: > 0 } && !c.ExpectAny.Any(e => lower.Contains(e.ToLowerInvariant()))) return false;
        if (c.ExpectAll is { Length: > 0 } && !c.ExpectAll.All(e => lower.Contains(e.ToLowerInvariant()))) return false;
        if (c.ExpectNone is { Length: > 0 } && c.ExpectNone.Any(e => lower.Contains(e.ToLowerInvariant()))) return false;
        if (c.ExpectRegex is { } rx && !System.Text.RegularExpressions.Regex.IsMatch(answer, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) return false;
        if (c.MaxWords is { } max && answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > max) return false;
        if (c.MinQuestionMarks is { } q && answer.Count(ch => ch == '?') < q) return false;
        return true;
    }
}
