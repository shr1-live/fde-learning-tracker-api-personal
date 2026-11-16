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

/// <summary>Talks to any provider that offers the common /chat/completions API (Gemini, Groq, GitHub Models, Ollama, OpenAI...). Settings come only from environment variables.</summary>
public sealed class OpenAiCompatibleClient(HttpClient http, AppOptions options) : ILlmClient
{
    const int MaxAttempts = 3;

    public bool IsConfigured => options.LlmBaseUrl is not null && options.LlmApiKey is not null && options.LlmModel is not null;
    public string? Model => options.LlmModel;

    public async Task<LlmResult> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var url = options.LlmBaseUrl!.TrimEnd('/') + "/chat/completions";
        var body = JsonSerializer.Serialize(new
        {
            model = options.LlmModel,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        });

        for (var attempt = 1; ; attempt++)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.LlmTimeoutSeconds));
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.LlmApiKey);
                using var res = await http.SendAsync(req, timeout.Token);
                var text = await res.Content.ReadAsStringAsync(timeout.Token);
                var code = (int)res.StatusCode;
                if (res.IsSuccessStatusCode) return Parse(text, (int)clock.ElapsedMilliseconds);
                if (attempt < MaxAttempts && (code == 429 || code >= 500)) { await Task.Delay(TimeSpan.FromSeconds(attempt), ct); continue; }
                throw new LlmException("provider_error", $"The provider answered {code}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException("timeout", $"No answer within {options.LlmTimeoutSeconds} seconds");
            }
            catch (HttpRequestException)
            {
                if (attempt < MaxAttempts) { await Task.Delay(TimeSpan.FromSeconds(attempt), ct); continue; }
                throw new LlmException("provider_error", "Could not reach the provider");
            }
        }
    }

    LlmResult Parse(string json, int latencyMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            int Tokens(string name) => root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var t) && t.TryGetInt32(out var n) ? n : 0;
            var model = root.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } s ? s : options.LlmModel!;
            return new LlmResult(content, Tokens("prompt_tokens"), Tokens("completion_tokens"), latencyMs, model);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new LlmException("provider_error", "The provider's answer was not in the expected format");
        }
    }
}
