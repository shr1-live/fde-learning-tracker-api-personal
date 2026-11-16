using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FdeTracker.Api.Common;

namespace FdeTracker.Api.Services.Llm;

public sealed record LlmResult(string Text, int InputTokens, int OutputTokens, int LatencyMs, string Model);

/// <summary>A provider failure. Kind is "timeout" or "provider_error". The message is safe to log: it never holds the key or the provider's body.</summary>
public sealed class LlmException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

public interface ILlmClient
{
    bool IsConfigured { get; }
    string? Model { get; }
    Task<LlmResult> CompleteAsync(string system, string user, CancellationToken ct);
}

/// <summary>
/// Shared plumbing for HTTP model providers: timeout, retry on 429/5xx/network errors, latency across all attempts.
/// A provider only says how to build its request and how to read its answer.
/// </summary>
public abstract class HttpLlmClient(HttpClient http, AppOptions options) : ILlmClient
{
    const int MaxAttempts = 3;

    protected AppOptions Options => options;
    public abstract bool IsConfigured { get; }
    public string? Model => options.LlmModel;

    protected abstract HttpRequestMessage BuildRequest(string system, string user);
    protected abstract LlmResult Parse(string json, int latencyMs);

    public async Task<LlmResult> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew(); // latency covers every attempt, not just the last one
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.LlmTimeoutSeconds));
                using var req = BuildRequest(system, user);
                using var res = await http.SendAsync(req, timeout.Token);
                var text = await res.Content.ReadAsStringAsync(timeout.Token);
                var code = (int)res.StatusCode;
                if (res.IsSuccessStatusCode) return Parse(text, (int)clock.ElapsedMilliseconds);
                if (attempt < MaxAttempts && (code == 429 || code >= 500)) { await Task.Delay(RetryDelay(attempt), ct); continue; }
                throw new LlmException("provider_error", $"The provider answered {code}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException("timeout", $"No answer within {options.LlmTimeoutSeconds} seconds");
            }
            catch (HttpRequestException)
            {
                if (attempt < MaxAttempts) { await Task.Delay(RetryDelay(attempt), ct); continue; }
                throw new LlmException("provider_error", "Could not reach the provider");
            }
        }
    }

    /// <summary>Wait before the next try: LLM_RETRY_DELAY_MS times the attempt number (default 1000).</summary>
    protected virtual TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(options.LlmRetryDelayMs * attempt);

    /// <summary>Token count from the "usage" object, or 0 when the provider does not report it.</summary>
    protected static int Usage(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var t) && t.TryGetInt32(out var n) ? n : 0;

    /// <summary>The model name the provider reports, falling back to the configured one.</summary>
    protected string ModelOf(JsonElement root) =>
        root.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } s ? s : Options.LlmModel!;

    protected static LlmException BadFormat() => new("provider_error", "The provider's answer was not in the expected format");
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
            max_tokens = 1500, // bounds the cost of one answer
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
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return new LlmResult(content, Usage(root, "prompt_tokens"), Usage(root, "completion_tokens"), latencyMs, ModelOf(root));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { throw BadFormat(); }
    }
}
