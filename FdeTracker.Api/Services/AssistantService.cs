using System.Text;
using FdeTracker.Api.Common;
using FdeTracker.Api.Data;
using FdeTracker.Api.Models.Requests;
using FdeTracker.Api.Repositories;
using FdeTracker.Api.Services.Llm;
using FdeTracker.Api.ViewModels;

namespace FdeTracker.Api.Services;

/// <summary>Recruitment candidate analysis: builds the prompt, calls the model, then checks the answer before anyone sees it.</summary>
public sealed class AssistantService(Db db, ILlmClient llm, PromptLibrary prompts, LlmCallRepository calls, AppOptions options, ILogger<AssistantService> logger)
{
    public async Task<AssistantUsageView> Usage(Guid userId) => new()
    {
        Configured = llm.IsConfigured, Model = llm.Model, DailyCap = options.LlmDailyCap,
        UsedToday = await db.Read(s => calls.CountLastDay(s, userId)), PromptVersions = PromptLibrary.Versions,
    };

    public async Task<AnalysisView> Analyze(Guid userId, AnalyzeRequest req, CancellationToken ct)
    {
        if (!llm.IsConfigured) throw new ApiException(503, "The assistant is not set up yet");
        if (await db.Read(s => calls.CountLastDay(s, userId)) >= options.LlmDailyCap)
            throw new ApiException(429, $"Daily limit of {options.LlmDailyCap} analyses reached. Try again tomorrow.");

        var version = req.PromptVersion ?? PromptLibrary.DefaultVersion;
        var required = SkillTools.ExtractSkills(req.JobDescription);
        LlmResult result;
        try
        {
            result = await llm.CompleteAsync(prompts.System(version), BuildUserMessage(req), ct);
        }
        catch (LlmException e)
        {
            logger.LogWarning("LLM call failed: {Kind}: {Message}", e.Kind, e.Message);
            await Log(userId, llm.Model, version, null, null, null, e.Kind);
            throw e.Kind == "timeout" ? new ApiException(504, "The model took too long. Try again.") : new ApiException(502, "The model service is not available. Try again later.");
        }

        try
        {
            var (candidates, warnings) = AnalysisParser.Parse(result.Text, req.Candidates, required);
            await Log(userId, result.Model, version, result.InputTokens, result.OutputTokens, result.LatencyMs, "ok");
            return new AnalysisView
            {
                PromptVersion = version, Model = result.Model, LatencyMs = result.LatencyMs, InputTokens = result.InputTokens, OutputTokens = result.OutputTokens,
                RequiredSkills = required, Candidates = candidates, Warnings = warnings,
            };
        }
        catch (UnusableOutputException e)
        {
            logger.LogWarning("LLM answer refused: {Reason}", e.Message);
            await Log(userId, result.Model, version, result.InputTokens, result.OutputTokens, result.LatencyMs, "invalid_output");
            throw new ApiException(502, "The model gave an answer we could not use. Try again.");
        }
    }

    Task Log(Guid userId, string? model, string version, int? inTokens, int? outTokens, int? latency, string status) =>
        db.Read(async s => { await calls.Insert(s, userId, model, version, inTokens, outTokens, latency, status); return 0; });

    /// <summary>All job and candidate text is wrapped in tags with angle brackets escaped: it is data, and it cannot close a tag or fake one.</summary>
    static string BuildUserMessage(AnalyzeRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<job_description>").AppendLine(PromptText.Escape(req.JobDescription)).AppendLine("</job_description>");
        for (var i = 0; i < req.Candidates.Count; i++)
        {
            var c = req.Candidates[i];
            sb.AppendLine($"<candidate id=\"{i + 1}\" name=\"{PromptText.Escape(c.Name).Replace('"', '\'')}\">").AppendLine(PromptText.Escape(c.Profile)).AppendLine("</candidate>");
        }
        return sb.ToString();
    }
}
