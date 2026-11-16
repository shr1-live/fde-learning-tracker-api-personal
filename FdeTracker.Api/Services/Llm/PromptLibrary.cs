namespace FdeTracker.Api.Services.Llm;

/// <summary>The stored prompt versions (Prompts/analyze.vN.md). Keeping every version in the repo lets us compare them on the same test cases.</summary>
public sealed class PromptLibrary
{
    public const string DefaultVersion = "v2"; // best overall in the Day 2 comparison (docs/evals/day2-local-comparison.md)
    public static readonly string[] Versions = ["v1", "v2", "v3"];

    readonly Dictionary<string, string> _prompts;

    public PromptLibrary()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Prompts");
        _prompts = Versions.ToDictionary(v => v, v => File.ReadAllText(Path.Combine(dir, $"analyze.{v}.md")).Trim());
    }

    public string System(string version) => _prompts[version];
}
