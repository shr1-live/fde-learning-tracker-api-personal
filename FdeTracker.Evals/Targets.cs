using System.Text.Json;
using System.Text.Json.Serialization;

namespace FdeTracker.Evals;

/// <summary>One model to test. The API key is never stored here: only the NAME of the environment variable that holds it.</summary>
public sealed class Target
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("api_key_env")] public string? ApiKeyEnv { get; set; }
    /// <summary>"openai" (default) = any chat-completions provider; "anthropic" = Claude.</summary>
    [JsonPropertyName("provider")] public string Provider { get; set; } = "openai";
}

public sealed record ResolvedTarget(string Name, string BaseUrl, string Model, string ApiKey, string Provider);

public static class Targets
{
    /// <summary>
    /// Reads the targets file and looks up each key in the environment. A target with no key is skipped with a message,
    /// except local ones (localhost), which need none. Returns what can run.
    /// </summary>
    public static List<ResolvedTarget> Load(string path, string[]? only)
    {
        List<Target> all;
        try { all = JsonSerializer.Deserialize<List<Target>>(File.ReadAllText(path)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not read the targets file '{path}': {e.Message}");
            return [];
        }
        var run = new List<ResolvedTarget>();
        foreach (var t in all.Where(t => only is null || only.Contains(t.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var local = t.BaseUrl.Contains("127.0.0.1") || t.BaseUrl.Contains("localhost");
            var key = t.ApiKeyEnv is { Length: > 0 } ? Environment.GetEnvironmentVariable(t.ApiKeyEnv) : null;
            if (string.IsNullOrWhiteSpace(key) && !local)
            {
                Console.WriteLine($"Skipping {t.Name}: set the environment variable {t.ApiKeyEnv ?? "(api_key_env missing)"} to include it.");
                continue;
            }
            run.Add(new ResolvedTarget(t.Name, t.BaseUrl, t.Model, string.IsNullOrWhiteSpace(key) ? "local" : key, t.Provider.ToLowerInvariant()));
        }
        return run;
    }
}
