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
        var version = req.PromptVersion ?? PromptLibrary.DefaultVersion;

        // Reserve the call before anything is sent, under a per-person lock: parallel or aborted requests still count.
        var callId = await db.Tx(async s =>
        {
            await calls.LockUser(s, userId);
            if (await calls.CountLastDay(s, userId) >= options.LlmDailyCap)
                throw new ApiException(429, $"Daily limit of {options.LlmDailyCap} analyses reached. Try again tomorrow.");
            return await calls.Reserve(s, userId, version);
        });

        var required = SkillTools.ExtractSkills(req.JobDescription);
        LlmResult result;
        try
        {
            result = await llm.CompleteAsync(prompts.System(version), BuildUserMessage(req), ct);
        }
        catch (LlmException e)
        {
            logger.LogWarning("LLM call failed: {Kind}: {Message}", e.Kind, e.Message);
            await Finish(callId, llm.Model, null, null, null, e.Kind);
            throw LlmFailure.ToApi(e);
        }
        catch (OperationCanceledException)
        {
            await Finish(callId, llm.Model, null, null, null, "cancelled"); // the caller left; the reserved call still counts
            throw;
        }

        try
        {
            var (candidates, warnings) = AnalysisParser.Parse(result.Text, req.Candidates, required);
            await Finish(callId, result.Model, result.InputTokens, result.OutputTokens, result.LatencyMs, "ok");
            return new AnalysisView
            {
                PromptVersion = version, Model = result.Model, LatencyMs = result.LatencyMs, InputTokens = result.InputTokens, OutputTokens = result.OutputTokens,
                RequiredSkills = required, Candidates = candidates, Warnings = warnings,
            };
        }
        catch (UnusableOutputException e)
        {
            logger.LogWarning("LLM answer refused: {Reason}", e.Message);
            await Finish(callId, result.Model, result.InputTokens, result.OutputTokens, result.LatencyMs, "invalid_output");
            throw new ApiException(502, "The model gave an answer we could not use. Try again.");
        }
    }

    Task Finish(long callId, string? model, int? inTokens, int? outTokens, int? latency, string status) =>
        db.Read(async s => { await calls.Finish(s, callId, model, inTokens, outTokens, latency, status); return 0; });

    /// <summary>All job and candidate text is wrapped in tags with angle brackets escaped: it is data, and it cannot close a tag or fake one.</summary>
    static string BuildUserMessage(AnalyzeRequest req) => PromptText.UserMessage(req.JobDescription, req.Candidates);
}
