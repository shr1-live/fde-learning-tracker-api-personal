using System.ComponentModel.DataAnnotations;

namespace FdeTracker.Api.Models.Requests;

public static class SubmissionPatterns
{
    public const string TaskKey = "^[a-z0-9-]{3,60}\\z";
}

public sealed class SaveNoteRequest
{
    string? _note;
    [StringLength(2000)] public string? Note { get => _note; set => _note = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
}

public sealed class AddLinkRequest
{
    string _label = "";
    string _url = "";
    [Required, StringLength(120, MinimumLength = 1)] public string Label { get => _label; set => _label = value?.Trim() ?? ""; }
    [Required, StringLength(500), HttpsUrl] public string Url { get => _url; set => _url = value?.Trim() ?? ""; }
}

public sealed class ReviewSubmissionRequest
{
    string? _comment;
    [Required, RegularExpression("^(approve|request_changes)\\z")] public string Decision { get; set; } = "";
    [StringLength(2000)] public string? Comment { get => _comment; set => _comment = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
}
