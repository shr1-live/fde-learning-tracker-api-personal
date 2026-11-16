using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FdeTracker.Api.Models.Requests;

public sealed class UpdateMeRequest
{
    string? _stack;
    [StringLength(80)] public string? DisplayName { get; set; }
    [RegularExpression(ContentPatterns.Stack)] public string? StackId { get => _stack; set { _stack = value; StackIdSet = true; } }
    [JsonIgnore] public bool StackIdSet { get; private set; }
}

public sealed class SetAccessRequest
{
    [Required, RegularExpression("^(approved|rejected|pending)\\z")] public string Status { get; set; } = "";
}

public sealed class SetRoleRequest
{
    [Required, RegularExpression("^(member|reviewer)\\z")] public string Role { get; set; } = "";
}

public sealed class ReviewDecisionRequest
{
    string? _comment;
    [Required, RegularExpression("^(approve|request_changes|reject)\\z")] public string Decision { get; set; } = "";
    [StringLength(2000)] public string? Comment { get => _comment; set => _comment = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
}

public sealed class CreateProgramRequest
{
    [Required, StringLength(120, MinimumLength = 2)] public string Title { get; set; } = "";
    [StringLength(600)] public string? Description { get; set; }
}

public sealed class AddItemRequest
{
    [Required, StringLength(140, MinimumLength = 1)] public string Title { get; set; } = "";
    [StringLength(600)] public string? Description { get; set; }
}

public sealed class SetProgressRequest
{
    public required bool Done { get; set; }
}
