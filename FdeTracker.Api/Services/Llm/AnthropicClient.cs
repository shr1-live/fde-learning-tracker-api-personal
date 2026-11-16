using System.Text;
using System.Text.Json;
using FdeTracker.Api.Common;

namespace FdeTracker.Api.Services.Llm;

/// <summary>
/// Claude through Anthropic's Messages API. The key comes only from LLM_API_KEY (an environment variable).
/// The base address is optional and defaults to Anthropic's own.
/// </summary>
public sealed class AnthropicClient(HttpClient http, AppOptions options) : HttpLlmClient(http, options)
{
    const string DefaultBaseUrl = "https://api.anthropic.com";
    const string ApiVersion = "2023-06-01";

    public override bool IsConfigured => Options.LlmApiKey is not null && Options.LlmModel is not null;

    protected override HttpRequestMessage BuildRequest(string system, string user)
    {
        // No "JSON mode" switch here: the prompt asks for JSON and AnalysisParser tolerates extra words around it.
        var body = JsonSerializer.Serialize(new
        {
            model = Options.LlmModel,
            max_tokens = 1500, // bounds the cost of one answer
            temperature = 0,
            system,
            messages = new object[] { new { role = "user", content = user } },
        });
        var req = new HttpRequestMessage(HttpMethod.Post, (Options.LlmBaseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/v1/messages") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", Options.LlmApiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
        return req;
    }

    protected override LlmResult Parse(string json, int latencyMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = new StringBuilder();
            foreach (var block in root.GetProperty("content").EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var tx)) text.Append(tx.GetString());
            int Tokens(string name) => root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var n) && n.TryGetInt32(out var v) ? v : 0;
            var model = root.TryGetProperty("model", out var m) && m.GetString() is { Length: > 0 } s ? s : Options.LlmModel!;
            return new LlmResult(text.ToString(), Tokens("input_tokens"), Tokens("output_tokens"), latencyMs, model);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { throw BadFormat(); }
    }
}

public static class LlmClientFactory
{
    public static readonly string[] Providers = ["openai", "anthropic"];

    /// <summary>"openai" means any provider with the common chat-completions API; "anthropic" means Claude.</summary>
    public static ILlmClient Create(string provider, HttpClient http, AppOptions options) => provider switch
    {
        "anthropic" => new AnthropicClient(http, options),
        "openai" => new OpenAiCompatibleClient(http, options),
        _ => throw new InvalidOperationException($"Unknown LLM provider '{provider}'. Use one of: {string.Join(", ", Providers)}"),
    };
}
