using System.Text.Json;
using FdeTracker.Evals;

// Usage: dotnet run --project FdeTracker.Evals -- day1
// Settings come from environment variables (never from code):
//   EVAL_BASE_URL   default http://127.0.0.1:11434/v1  (a local Ollama)
//   EVAL_API_KEY    default "local" (Ollama ignores it; use a real key only through this variable)
//   EVAL_MODELS     default qwen2.5:3b,qwen2.5:1.5b
//   EVAL_TIMEOUT    seconds per call, default 180
//   EVAL_OUT        output folder, default docs/evals
var command = args.FirstOrDefault() ?? "day1";
var baseUrl = Environment.GetEnvironmentVariable("EVAL_BASE_URL") ?? "http://127.0.0.1:11434/v1";
var apiKey = Environment.GetEnvironmentVariable("EVAL_API_KEY") ?? "local";
var models = (Environment.GetEnvironmentVariable("EVAL_MODELS") ?? "qwen2.5:3b,qwen2.5:1.5b").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var timeout = int.TryParse(Environment.GetEnvironmentVariable("EVAL_TIMEOUT"), out var t) && t > 0 ? t : 180;
var outDir = Environment.GetEnvironmentVariable("EVAL_OUT") ?? Path.Combine("docs", "evals");
Directory.CreateDirectory(outDir);

switch (command)
{
    case "day1":
        var set = JsonSerializer.Deserialize<PromptSet>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "evals", "day1-prompts.json")))!;
        var results = await Day1Runner.Run(set, baseUrl, apiKey, models, timeout);
        File.WriteAllText(Path.Combine(outDir, "day1-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "day1-model-comparison.md"), Report.Day1(results, models, set));
        Console.WriteLine($"\nWrote {Path.Combine(outDir, "day1-model-comparison.md")}");
        break;
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Available: day1");
        return 1;
}
return 0;
