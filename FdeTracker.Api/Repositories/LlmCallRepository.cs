using FdeTracker.Api.Data;

namespace FdeTracker.Api.Repositories;

public sealed class LlmCallRepository
{
    /// <summary>Serialises one person's reservations so the cap check and the insert cannot interleave.</summary>
    public Task LockUser(Session s, Guid userId) => s.Exec("select pg_advisory_xact_lock(hashtext(@key))", new { key = "llm:" + userId });

    /// <summary>Calls in the last 24 hours, reserved and failed ones included, so a loop of bad requests cannot run up the bill.</summary>
    public Task<int> CountLastDay(Session s, Guid userId) =>
        s.Scalar<int>("select count(*)::int from llm_calls where user_id = @userId and created_at > now() - interval '1 day'", new { userId });

    public Task<long> Reserve(Session s, Guid userId, string promptVersion) =>
        s.Scalar<long>("insert into llm_calls (user_id, prompt_version, status) values (@userId, @promptVersion, 'pending') returning id", new { userId, promptVersion });

    public Task Finish(Session s, long id, string? model, int? inputTokens, int? outputTokens, int? latencyMs, string status) =>
        s.Exec("update llm_calls set model = @model, input_tokens = @inputTokens, output_tokens = @outputTokens, latency_ms = @latencyMs, status = @status where id = @id",
            new { id, model, inputTokens, outputTokens, latencyMs, status });
}
