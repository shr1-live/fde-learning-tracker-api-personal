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
public sealed record Day2Row(string Model, string Version, int Repeat, string Candidate, bool Parsed, bool Present, bool Grounded, bool FitOk, bool NoForbidden, bool IncompleteOk, string Fit, string[] Skills, int LatencyMs, int InputTokens, int OutputTokens, string? Error);

/// <summary>Day 2: every stored prompt version, on the same job and candidates, repeated to see whether results stay the same.</summary>
public static class Day2Runner
{
    public const int Repeats = 2;

    public static async Task<List<Day2Row>> Run(Day2Cases cases, IReadOnlyList<ResolvedTarget> targets, int timeoutSeconds)
    {
        var rows = new List<Day2Row>();
        var people = cases.Candidates.Select(c => new CandidateInput { Name = c.Name, Profile = c.Profile }).ToList();
        var required = SkillTools.ExtractSkills(cases.JobDescription);
        var user = PromptText.UserMessage(cases.JobDescription, people);
        var prompts = new PromptLibrary();
        using var http = new HttpClient(new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };

        foreach (var target in targets)
        {
            var client = LlmClientFactory.Create(target.Provider, http, new AppOptions { LlmProvider = target.Provider, LlmBaseUrl = string.IsNullOrWhiteSpace(target.BaseUrl) ? null : target.BaseUrl, LlmApiKey = target.ApiKey, LlmModel = target.Model, LlmTimeoutSeconds = timeoutSeconds });
            foreach (var version in PromptLibrary.Versions)
                for (var repeat = 1; repeat <= Repeats; repeat++)
                {
                    Console.Write($"{target.Name,-14} {version} run {repeat}  ");
                    try
                    {
                        var r = await client.CompleteAsync(prompts.System(version), user, CancellationToken.None);
                        rows.AddRange(Score(target.Name, version, repeat, cases, people, required, r));
                        Console.WriteLine($"ok  {r.LatencyMs} ms");
                    }
                    catch (Exception e) when (e is LlmException or HttpRequestException or TaskCanceledException)
                    {
                        rows.AddRange(cases.Candidates.Select(c => Failed(target.Name, version, repeat, c.Name, e is LlmException le ? $"{le.Kind}: {le.Message}" : e.GetType().Name)));
                        Console.WriteLine("ERROR");
                    }
                }
        }
        return rows;
    }

    static Day2Row Failed(string model, string version, int repeat, string name, string error) =>
        new(model, version, repeat, name, false, false, false, false, false, false, "", [], 0, 0, 0, error);

    static IEnumerable<Day2Row> Score(string model, string version, int repeat, Day2Cases cases, List<CandidateInput> people, List<string> required, LlmResult r)
    {
        List<CandidateAnalysisViewLite> parsed;
        try
        {
            var (views, _) = AnalysisParser.Parse(r.Text, people, required);
            parsed = views.Select(v => new CandidateAnalysisViewLite(v.Id, v.Fit, v.MatchedSkills.Select(m => m.Skill).ToArray(), v.NotStated.Count, v.RemovedClaims)).ToList();
        }
        catch (UnusableOutputException)
        {
            return cases.Candidates.Select(c => Failed(model, version, repeat, c.Name, "unusable output")).ToList();
        }

        return cases.Candidates.Select((c, i) =>
        {
            var v = parsed.FirstOrDefault(x => x.Id == i + 1);
            var present = v is not null;
            var grounded = present && v!.RemovedClaims == 0; // structured count from the parser, not text matching
            var fitOk = present && c.Expect.Fit.Contains(v!.Fit);
            var clean = present && !v!.Skills.Any(s => c.Expect.MustNotClaim.Contains(s, StringComparer.OrdinalIgnoreCase));
            var incompleteOk = !c.Expect.Incomplete || (present && (v!.Fit == "insufficient_information" || v.NotStatedCount > 0));
            return new Day2Row(model, version, repeat, c.Name, true, present, grounded, fitOk, clean, incompleteOk, v?.Fit ?? "", v?.Skills ?? [], r.LatencyMs, r.InputTokens, r.OutputTokens, null);
        }).ToList();
    }

    sealed record CandidateAnalysisViewLite(int Id, string Fit, string[] Skills, int NotStatedCount, int RemovedClaims);
}
