using System.ComponentModel.DataAnnotations;

namespace FdeTracker.Api.Models.Requests;

public sealed class CandidateInput
{
    string _name = "";
    string _profile = "";
    [Required, StringLength(80, MinimumLength = 1)] public string Name { get => _name; set => _name = value?.Trim() ?? ""; }
    [Required, StringLength(4000, MinimumLength = 20)] public string Profile { get => _profile; set => _profile = value?.Trim() ?? ""; }
}

public sealed class AnalyzeRequest
{
    string _jobDescription = "";
    [Required, StringLength(6000, MinimumLength = 20)] public string JobDescription { get => _jobDescription; set => _jobDescription = value?.Trim() ?? ""; }
    [Required, MinLength(1), MaxLength(5)] public List<CandidateInput> Candidates { get; set; } = [];
    [RegularExpression("^v[1-3]$")] public string? PromptVersion { get; set; }
}
