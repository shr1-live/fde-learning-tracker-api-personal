using System.Text.RegularExpressions;

namespace FdeTracker.Api.Services.Llm;

/// <summary>
/// Two plain functions that run in our own code (never in the model): find known skills in a text, and score a match.
/// They give an independent answer to compare with what the model says.
/// </summary>
public static class SkillTools
{
    // canonical name -> spellings to look for
    static readonly Dictionary<string, string[]> Vocabulary = new()
    {
        ["C#"] = ["c#", "csharp"], [".NET"] = [".net", "dotnet", "asp.net"], ["Java"] = ["java"], ["Spring Boot"] = ["spring boot", "spring"],
        ["Python"] = ["python"], ["Node.js"] = ["node.js", "nodejs", "node"], ["JavaScript"] = ["javascript", "js"], ["TypeScript"] = ["typescript"],
        ["React"] = ["react"], ["Angular"] = ["angular"], ["SQL"] = ["sql"], ["PostgreSQL"] = ["postgresql", "postgres"], ["MongoDB"] = ["mongodb", "mongo"],
        ["Docker"] = ["docker"], ["Kubernetes"] = ["kubernetes", "k8s"], ["AWS"] = ["aws"], ["Azure"] = ["azure"], ["GCP"] = ["gcp", "google cloud"],
        ["REST APIs"] = ["rest", "restful", "rest api", "rest apis"], ["GraphQL"] = ["graphql"], ["Git"] = ["git"], ["CI/CD"] = ["ci/cd", "ci cd", "continuous integration"],
        ["Testing"] = ["unit test", "unit tests", "testing", "xunit", "jest", "pytest"], ["LLM"] = ["llm", "large language model"], ["RAG"] = ["rag", "retrieval-augmented"],
        ["Prompt engineering"] = ["prompt engineering", "prompt"], ["Vector databases"] = ["vector database", "vector db", "embeddings"], ["Machine learning"] = ["machine learning", "ml"],
        ["Linux"] = ["linux"], ["Communication"] = ["communication", "stakeholder"], ["Leadership"] = ["leadership", "mentoring", "team lead"],
    };

    public static List<string> ExtractSkills(string text)
    {
        var lower = text.ToLowerInvariant();
        return Vocabulary
            .Where(kv => kv.Value.Any(alias => Regex.IsMatch(lower, $@"(?<![a-z0-9+#.]){Regex.Escape(alias)}(?![a-z0-9+#])")))
            .Select(kv => kv.Key)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    public static (List<string> Matched, List<string> Missing, int Score) ScoreMatch(IReadOnlyCollection<string> required, IReadOnlyCollection<string> found)
    {
        var have = found.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = required.Where(have.Contains).ToList();
        var missing = required.Where(r => !have.Contains(r)).ToList();
        var score = required.Count == 0 ? 0 : (int)Math.Round(100.0 * matched.Count / required.Count);
        return (matched, missing, score);
    }
}
