using System.Text.Json;
using System.Text.RegularExpressions;
using FdeTracker.Api.Models.Requests;
using FdeTracker.Api.ViewModels;

namespace FdeTracker.Api.Services.Llm;

public sealed class UnusableOutputException(string message) : Exception(message);

/// <summary>
/// Turns the model's text into checked data. Anything that is not in the agreed shape is refused, and every
/// "evidence" quote must really appear in that candidate's profile; claims without evidence are removed.
/// </summary>
public static partial class AnalysisParser
{
    static readonly string[] Fits = ["strong", "partial", "weak", "insufficient_information"];

    public static (List<CandidateAnalysisView> Candidates, List<string> Warnings) Parse(string raw, IReadOnlyList<CandidateInput> input, List<string> requiredSkills)
    {
        var text = Fence().Replace(raw.Trim(), "").Trim();
        // Some models add words around the JSON, so look for the object that holds the answer.
        var doc = TryParse(text) ?? FindAnswerObject(text) ?? throw new UnusableOutputException("Not valid JSON");
        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("candidates", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new UnusableOutputException("Missing candidates list");

        var warnings = new List<string>();
        var byId = new Dictionary<int, CandidateAnalysisView>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id) || id < 1 || id > input.Count)
            {
                warnings.Add("An entry with an unknown candidate id was ignored.");
                continue;
            }
            if (byId.ContainsKey(id))
            {
                warnings.Add($"{input[id - 1].Name}: a second analysis for the same candidate was ignored.");
                continue;
            }
            byId[id] = ReadCandidate(item, id, input[id - 1], requiredSkills, warnings);
        }
        if (byId.Count == 0) throw new UnusableOutputException("No usable candidate in the answer");
        for (var i = 1; i <= input.Count; i++)
            if (!byId.ContainsKey(i)) warnings.Add($"{input[i - 1].Name}: the model returned no analysis for this candidate.");
        return (byId.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList(), warnings);
    }

    /// <summary>One candidate's analysis, with every quoted claim checked against that candidate's own profile.</summary>
    static CandidateAnalysisView ReadCandidate(JsonElement item, int id, CandidateInput person, List<string> requiredSkills, List<string> warnings)
    {
        var (matched, removed) = CheckClaims(item, person.Profile);
        if (removed > 0) warnings.Add($"{person.Name}: {removed} claim(s) removed because the quoted evidence is not in the profile.");
        var fit = Text(item, "fit", 40);
        var (found, missing, score) = SkillTools.ScoreMatch(requiredSkills, SkillTools.ExtractSkills(person.Profile));
        return new CandidateAnalysisView
        {
            Id = id, Name = person.Name,
            Summary = Text(item, "summary", 600),
            MatchedSkills = matched,
            Gaps = Strings(item, "gaps"),
            NotStated = Strings(item, "not_stated"),
            Fit = Fits.Contains(fit) ? fit : "insufficient_information",
            Check = new SkillCheckView { Matched = found, Missing = missing, Score = score },
        };
    }

    static (List<MatchedSkillView> Kept, int Removed) CheckClaims(JsonElement item, string profile)
    {
        var kept = new List<MatchedSkillView>();
        var removed = 0;
        foreach (var m in Array(item, "matched_skills"))
        {
            var skill = Text(m, "skill", 80);
            var evidence = Text(m, "evidence", 300);
            if (skill.Length == 0) continue;
            if (IsQuoted(evidence, profile) && Supports(skill, evidence)) kept.Add(new MatchedSkillView { Skill = skill, Evidence = evidence });
            else removed++;
        }
        return (kept, removed);
    }

    /// <summary>The quote must be at least 8 characters and appear in the profile (ignoring case and spacing). The profile reaches the model with &lt; and &gt; escaped, so that spelling is accepted too.</summary>
    static bool IsQuoted(string evidence, string profile)
    {
        var e = Normalize(evidence);
        if (e.Length < 8) return false;
        return Normalize(profile).Contains(e, StringComparison.Ordinal) || Normalize(PromptText.Escape(profile)).Contains(e, StringComparison.Ordinal);
    }

    /// <summary>For skills we know, the quote must mention that skill ("Kubernetes" cannot be backed by a quote about Docker). Other skills only need a real quote.</summary>
    static bool Supports(string skill, string evidence) =>
        !SkillTools.IsKnownSkill(skill) || SkillTools.ExtractSkills(evidence).Contains(skill, StringComparer.OrdinalIgnoreCase);

    static string Normalize(string s) => Spaces().Replace(s, " ").Trim().ToLowerInvariant();
    static JsonDocument? TryParse(string s)
    {
        try { return JsonDocument.Parse(s); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Walks the text for balanced { ... } blocks (ignoring braces inside strings) and returns the first one that
    /// parses and has a "candidates" list. Stray braces in the model's own words are skipped.
    /// </summary>
    static JsonDocument? FindAnswerObject(string t)
    {
        for (var start = t.IndexOf('{'); start >= 0; start = t.IndexOf('{', start + 1))
        {
            int depth = 0; bool inString = false, escaped = false;
            for (var i = start; i < t.Length; i++)
            {
                var ch = t[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    var doc = TryParse(t[start..(i + 1)]);
                    if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("candidates", out _)) return doc;
                    doc?.Dispose();
                    break;
                }
            }
        }
        return null;
    }

    static IEnumerable<JsonElement> Array(JsonElement o, string name) =>
        o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(20) : [];

    static string Text(JsonElement o, string name, int max)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var s = (v.GetString() ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }

    static List<string> Strings(JsonElement o, string name) =>
        o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => (x.GetString() ?? "").Trim()).Where(x => x.Length > 0).Select(x => x.Length > 200 ? x[..200] : x).Take(15).ToList()
            : [];

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^```[a-zA-Z]*\s*|\s*```\z")] private static partial Regex Fence();
}

/// <summary>Candidate and job text is data. It is wrapped in tags and its angle brackets are escaped so it cannot close a tag or open a fake one.</summary>
public static class PromptText
{
    public static string Escape(string s) => s.Replace("<", "&lt;").Replace(">", "&gt;");
}
