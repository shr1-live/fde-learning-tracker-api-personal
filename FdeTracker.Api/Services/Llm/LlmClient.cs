using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FdeTracker.Api.Common;

namespace FdeTracker.Api.Services.Llm;

public sealed record LlmResult(string Text, int InputTokens, int OutputTokens, int LatencyMs, string Model);

public interface ILlmClient
{
    bool IsConfigured { get; }
    string? Model { get; }
    Task<LlmResult> CompleteAsync(string system, string user, CancellationToken ct);
}

/// <summary>
/// Shared plumbing for HTTP model providers. One call is: up to N attempts, each with its own time limit, all inside one overall deadline.
/// Retried: 429, 5xx and network failures, waiting what the provider asks (Retry-After, or the "retry in 28s" hint) or else a growing wait with a little randomness.
/// Not retried: a timeout (the time is spent), and anything that cannot get better by asking again (bad request, refused key, unknown model, blocked answer).
/// A provider only says how to build its request and how to read its answer.
/// </summary>
public abstract class HttpLlmClient(HttpClient http, AppOptions options) : ILlmClient
{
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);
    static readonly Regex RetryIn = new(@"retry in (\d+(?:\.\d+)?)\s*s|""retryDelay""\s*:\s*""(\d+(?:\.\d+)?)s""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    protected AppOptions Options => options;
    public abstract bool IsConfigured { get; }
    public string? Model => options.LlmModel;

    protected abstract HttpRequestMessage BuildRequest(string system, string user);
    protected abstract LlmResult Parse(string json, int latencyMs);

    public async Task<LlmResult> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew(); // latency and the deadline both cover every attempt, not just the last one
        var deadline = TimeSpan.FromSeconds(options.LlmTotalTimeoutSeconds);
        for (var attempt = 1; ; attempt++)
        {
            try { return await Attempt(system, user, clock, deadline, ct); }
            catch (LlmException e) when (e.Retryable && e.Kind != "timeout" && attempt < options.LlmMaxAttempts)
            {
                // The provider said how long to wait: wait that, unless it is longer than we are willing to, then the caller is told instead.
                if (e.RetryAfter is { } asked && asked > TimeSpan.FromSeconds(options.LlmMaxRetryAfterSeconds)) throw;
                var wait = e.RetryAfter ?? Backoff(attempt);
                if (clock.Elapsed + wait >= deadline) throw; // no point waiting past the overall deadline
                await options.Sleep(wait, ct);
            }
        }
    }

    async Task<LlmResult> Attempt(string system, string user, Stopwatch clock, TimeSpan deadline, CancellationToken ct)
    {
        var left = deadline - clock.Elapsed;
        if (left <= TimeSpan.Zero) throw LlmException.Timeout(options.LlmTotalTimeoutSeconds);
        var limit = TimeSpan.FromSeconds(options.LlmTimeoutSeconds);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(left < limit ? left : limit);
            using var req = BuildRequest(system, user);
            using var res = await http.SendAsync(req, timeout.Token);
            var text = await res.Content.ReadAsStringAsync(timeout.Token);
            if (res.IsSuccessStatusCode) return Parse(text, (int)clock.ElapsedMilliseconds);
            throw LlmException.FromStatus((int)res.StatusCode, RetryAfterOf(res, text));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw LlmException.Timeout(options.LlmTimeoutSeconds); }
        catch (HttpRequestException) { throw LlmException.Network(); }
    }

    /// <summary>Growing wait: base, 2x base, 4x base... up to 20 s, with up to 25% randomness so many callers do not retry in step.</summary>
    TimeSpan Backoff(int attempt)
    {
        var ms = Math.Min(options.LlmRetryDelayMs * Math.Pow(2, attempt - 1), MaxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms * (1 + (options.Jitter() - 0.5) * 0.5));
    }

    /// <summary>The wait a provider asks for: the Retry-After header, or the "retry in 28s" / retryDelay hint some providers (Gemini) put in the body. Only the number is kept, never the text.</summary>
    static TimeSpan? RetryAfterOf(HttpResponseMessage res, string body)
    {
        var header = res.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) { var d = date - DateTimeOffset.UtcNow; return d > TimeSpan.Zero ? d : TimeSpan.Zero; }
        try
        {
            var m = RetryIn.Match(body);
            if (m.Success && double.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds < 3600)
                return TimeSpan.FromSeconds(seconds);
        }
        catch (RegexMatchTimeoutException) { /* no hint: the normal backoff is used */ }
        return null;
    }

    /// <summary>Token count from the "usage" object, or 0 when the provider does not report it.</summary>
    protected static int Usage(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var t) && t.TryGetInt32(out var n) ? n : 0;

    /// <summary>The model name the provider reports, falling back to the configured one.</summary>
    protected string ModelOf(JsonElement root) =>
        root.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } s ? s : Options.LlmModel!;

    protected static LlmException BadFormat() => LlmException.BadFormat();
}

/// <summary>Any provider with the common /chat/completions API (Gemini, Groq, GitHub Models, Ollama, OpenAI...).</summary>
public sealed class OpenAiCompatibleClient(HttpClient http, AppOptions options) : HttpLlmClient(http, options)
{
    public override bool IsConfigured => Options.LlmBaseUrl is not null && Options.LlmApiKey is not null && Options.LlmModel is not null;

    protected override HttpRequestMessage BuildRequest(string system, string user)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = Options.LlmModel,
            temperature = 0,
            max_tokens = Options.LlmMaxTokens, // bounds the cost of one answer (LLM_MAX_TOKENS)
            response_format = new { type = "json_object" },
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        });
        var req = new HttpRequestMessage(HttpMethod.Post, Options.LlmBaseUrl!.TrimEnd('/') + "/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.LlmApiKey);
        return req;
    }

    protected override LlmResult Parse(string json, int latencyMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var choice = root.GetProperty("choices")[0];
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (finish is "content_filter" or "safety") throw LlmException.Blocked(); // the provider's safety system declined: retrying will not help
            var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            return new LlmResult(content, Usage(root, "prompt_tokens"), Usage(root, "completion_tokens"), latencyMs, ModelOf(root));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { throw BadFormat(); }
    }
}
