using FdeTracker.Api.Data;

namespace FdeTracker.Api.Repositories;

public sealed class LlmCallRepository
{
    public Task Insert(Session s, Guid userId, string? model, string promptVersion, int? inputTokens, int? outputTokens, int? latencyMs, string status) =>
        s.Exec(
            "insert into llm_calls (user_id, model, prompt_version, input_tokens, output_tokens, latency_ms, status) values (@userId, @model, @promptVersion, @inputTokens, @outputTokens, @latencyMs, @status)",
            new { userId, model, promptVersion, inputTokens, outputTokens, latencyMs, status });

    /// <summary>Calls in the last 24 hours, failed ones included, so a loop of bad requests cannot run up the bill.</summary>
    public Task<int> CountLastDay(Session s, Guid userId) =>
        s.Scalar<int>("select count(*)::int from llm_calls where user_id = @userId and created_at > now() - interval '1 day'", new { userId });
}
