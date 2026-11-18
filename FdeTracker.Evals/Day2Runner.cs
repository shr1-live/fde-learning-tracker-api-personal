using System.Text.Json.Serialization;
using FdeTracker.Api.Common;
using FdeTracker.Api.Models.Requests;
using FdeTracker.Api.Services.Llm;

namespace FdeTracker.Evals;

public sealed class Day2Expect
{
    [JsonPropertyName("fit")] public string[] Fit { get; set; } = [];
    [JsonPropertyName("must_not_claim")] public string[] MustNotClaim { get; set; } = [];
    [JsonPropertyName("incomplete")] public bool Incomplete { get; set; }
    [JsonPropertyName("injection")] public bool Injection { get; set; }
}

public sealed class Day2Candidate
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("profile")] public string Profile { get; set; } = "";
    [JsonPropertyName("expect")] public Day2Expect Expect { get; set; } = new();
}

public sealed class Day2Cases
{
    [JsonPropertyName("job_description")] public string JobDescription { get; set; } = "";
    [JsonPropertyName("candidates")] public List<Day2Candidate> Candidates { get; set; } = [];
}

/// <summary>One candidate in one call: which checks passed. Failed calls produce rows with Parsed = false.</summary>
public sealed record Day2Row(string Model, string Version, int Repeat, string Candidate, bool Parsed, bool Present, bool Grounded, bool FitOk, bool NoForbidden, bool IncompleteOk, string Fit, string[] Skills, int LatencyMs, int InputTokens, int OutputTokens, string? Error, int Batch = 0, bool Clarifies = false, bool HasReasoning = false);

/// <summary>Day 2: every stored prompt version, on the same job and candidates, repeated to see whether results stay the same.</summary>
public static class Day2Runner
{
    public const int Repeats = 2;
    public const int BatchSize = 4; // candidates per call, like a real request (the API allows up to 5)

    public static async Task<List<Day2Row>> Run(Day2Cases cases, IReadOnlyList<ResolvedTarget> targets, int timeoutSeconds)
    {
        var rows = new List<Day2Row>();
        var required = SkillTools.ExtractSkills(cases.JobDescription);
        var prompts = new PromptLibrary();
        // EVAL_VERSIONS=v5,v6,v7 runs only those (the earlier versions' results are kept from an earlier run); default is every version.
        var only = Environment.GetEnvironmentVariable("EVAL_VERSIONS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var versions = PromptLibrary.Versions.Where(v => only is null || only.Contains(v)).ToArray();
        using var http = new HttpClient(new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };

        foreach (var target in targets)
        {
            var client = LlmClientFactory.Create(target.Provider, http, new AppOptions { LlmProvider = target.Provider, LlmBaseUrl = string.IsNullOrWhiteSpace(target.BaseUrl) ? null : target.BaseUrl, LlmApiKey = target.ApiKey, LlmModel = target.Model, LlmTimeoutSeconds = timeoutSeconds, LlmTotalTimeoutSeconds = timeoutSeconds });
            var pipeline = new AnalysisPipeline(client, prompts);
            foreach (var version in versions)
                for (var repeat = 1; repeat <= Repeats; repeat++)
                    foreach (var (group, batch) in cases.Candidates.Chunk(BatchSize).Select((g, i) => (g, i + 1)))
                    {
                        var part = new Day2Cases { JobDescription = cases.JobDescription, Candidates = [.. group] };
                        var people = group.Select(c => new CandidateInput { Name = c.Name, Profile = c.Profile }).ToList();
                        Console.Write($"{target.Name,-14} {version} run {repeat} batch {batch}  ");
                        try
                        {
                            var r = await pipeline.Run(version, cases.JobDescription, people, CancellationToken.None);
                            rows.AddRange(Score(target.Name, version, repeat, part, people, required, r, batch));
                            Console.WriteLine($"ok  {r.LatencyMs} ms");
                        }
                        catch (Exception e) when (e is LlmException or HttpRequestException or TaskCanceledException)
                        {
                            rows.AddRange(group.Select(c => Failed(target.Name, version, repeat, c.Name, e is LlmException le ? $"{le.Kind}: {le.Message}" : e.GetType().Name, batch)));
                            Console.WriteLine("ERROR " + (e is LlmException le2 ? $"{le2.Kind}: {le2.Message}" : e.GetType().Name + ": " + e.Message));
                        }
                    }
        }
        return rows;
    }

    static Day2Row Failed(string model, string version, int repeat, string name, string error, int batch = 0) =>
        new(model, version, repeat, name, false, false, false, false, false, false, "", [], 0, 0, 0, error, batch);

    static IEnumerable<Day2Row> Score(string model, string version, int repeat, Day2Cases cases, List<CandidateInput> people, List<string> required, LlmResult r, int batch)
    {
        List<CandidateAnalysisViewLite> parsed;
        try
        {
            var (views, _) = AnalysisParser.Parse(r.Text, people, required);
            parsed = views.Select(v => new CandidateAnalysisViewLite(v.Id, v.Fit, v.MatchedSkills.Select(m => m.Skill).ToArray(), v.NotStated.Count, v.RemovedClaims, v.ClarifyingQuestions.Count > 0, v.Reasoning.Length > 0)).ToList();
        }
        catch (UnusableOutputException)
        {
            return cases.Candidates.Select(c => Failed(model, version, repeat, c.Name, "unusable output", batch)).ToList();
        }

        return cases.Candidates.Select((c, i) =>
        {
            var v = parsed.FirstOrDefault(x => x.Id == i + 1);
            var present = v is not null;
            var grounded = present && v!.RemovedClaims == 0; // structured count from the parser, not text matching
            var fitOk = present && c.Expect.Fit.Contains(v!.Fit);
            var clean = present && !v!.Skills.Any(s => c.Expect.MustNotClaim.Contains(s, StringComparer.OrdinalIgnoreCase));
            var incompleteOk = !c.Expect.Incomplete || (present && (v!.Fit == "insufficient_information" || v.NotStatedCount > 0));
            return new Day2Row(model, version, repeat, c.Name, true, present, grounded, fitOk, clean, incompleteOk, v?.Fit ?? "", v?.Skills ?? [], r.LatencyMs, r.InputTokens, r.OutputTokens, null, batch, v?.Clarifies ?? false, v?.HasReasoning ?? false);
        }).ToList();
    }

    sealed record CandidateAnalysisViewLite(int Id, string Fit, string[] Skills, int NotStatedCount, int RemovedClaims, bool Clarifies, bool HasReasoning);
}
