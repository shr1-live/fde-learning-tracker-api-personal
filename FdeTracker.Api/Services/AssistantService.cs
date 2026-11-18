using System.Text;
using FdeTracker.Api.Common;
using FdeTracker.Api.Data;
using FdeTracker.Api.Models.Requests;
using FdeTracker.Api.Repositories;
using FdeTracker.Api.Services.Llm;
using FdeTracker.Api.ViewModels;

namespace FdeTracker.Api.Services;

/// <summary>Recruitment candidate analysis: builds the prompt, calls the model, then checks the answer before anyone sees it.</summary>
public sealed class AssistantService(Db db, ILlmClient llm, LlmHttp llmHttp, AnalysisPipeline pipeline, LlmCallRepository calls, AppOptions options, ILogger<AssistantService> logger)
{
    public async Task<AssistantUsageView> Usage(Guid userId) => new()
    {
        Configured = llm.IsConfigured, Model = llm.Model, DailyCap = LlmKeyScope.CapFor(options), UsingOwnKey = LlmKeyScope.Current is not null,
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
            var cap = LlmKeyScope.CapFor(options); // own-key calls are not charged to the shared limit
            if (await calls.CountLastDay(s, userId) >= cap)
                throw new ApiException(429, $"Daily limit of {cap} analyses reached. Try again tomorrow.");
            return await calls.Reserve(s, userId, version);
        });

        var required = SkillTools.ExtractSkills(req.JobDescription);
        LlmResult result;
        try
        {
            result = await pipeline.Run(version, req.JobDescription, req.Candidates, ct);
        }
        catch (LlmException e)
        {
            logger.LogWarning("LLM call failed: {Kind}: {Message}", e.Kind, e.Message);
            await Finish(callId, llm.Model, null, null, null, e.Kind);
            throw LlmFailure.ToApi(e, LlmKeyScope.Current is not null);
        }
        catch (OperationCanceledException)
        {
            await Finish(callId, llm.Model, null, null, null, "cancelled"); // the caller left; the reserved call still counts
            throw;
        }

        try
        {
            var (candidates, warnings) = AnalysisParser.Parse(result.Text, req.Candidates, required, options.AssistantSummary == "code" ? GuardrailOptions.AllWithCodeSummary : GuardrailOptions.All);
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

    /// <summary>The models the person's own key can use (needs the X-Gemini-Key header). Also proves the key works.</summary>
    public async Task<GeminiModels> Models(CancellationToken ct)
    {
        var key = LlmKeyScope.Current ?? throw new ApiException(400, "Paste your Gemini key first.");
        try { return await GeminiCatalog.ListAsync(llmHttp.Client, key.ApiKey, ct); }
        catch (LlmException e) { throw LlmFailure.ToApi(e, ownKey: true); }
    }

    Task Finish(long callId, string? model, int? inTokens, int? outTokens, int? latency, string status) =>
        db.Read(async s => { await calls.Finish(s, callId, model, inTokens, outTokens, latency, status); return 0; });

    /// <summary>All job and candidate text is wrapped in tags with angle brackets escaped: it is data, and it cannot close a tag or fake one.</summary>
    static string BuildUserMessage(AnalyzeRequest req) => PromptText.UserMessage(req.JobDescription, req.Candidates);
}
