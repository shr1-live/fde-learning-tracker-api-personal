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
    [JsonPropertyName("max_words")] public int? MaxWords { get; set; }
    [JsonPropertyName("min_question_marks")] public int? MinQuestionMarks { get; set; }
}

public sealed class PromptSet
{
    [JsonPropertyName("system")] public string System { get; set; } = "";
    [JsonPropertyName("cases")] public List<PromptCase> Cases { get; set; } = [];
}

public sealed record CaseResult(string Model, string CaseId, string Kind, bool Passed, string Answer, int LatencyMs, int InputTokens, int OutputTokens, string? Error);

/// <summary>Day 1: run one fixed prompt set against several models and score each answer automatically.</summary>
public static class Day1Runner
{
    public static async Task<List<CaseResult>> Run(PromptSet set, string baseUrl, string apiKey, string[] models, int timeoutSeconds)
    {
        var results = new List<CaseResult>();
        using var http = new HttpClient(new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var model in models)
        {
            var client = new OpenAiCompatibleClient(http, new AppOptions { LlmBaseUrl = baseUrl, LlmApiKey = apiKey, LlmModel = model, LlmTimeoutSeconds = timeoutSeconds });
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
                catch (LlmException e)
                {
                    results.Add(new CaseResult(model, c.Id, c.Kind, false, "", 0, 0, 0, $"{e.Kind}: {e.Message}"));
                    Console.WriteLine($"ERROR {e.Kind}");
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
        if (c.MaxWords is { } max && answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > max) return false;
        if (c.MinQuestionMarks is { } q && answer.Count(ch => ch == '?') < q) return false;
        return true;
    }
}
