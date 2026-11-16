namespace FdeTracker.Api.Common;

/// <summary>An error that carries the HTTP status to send. Services throw it; the error middleware renders it.</summary>
public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    public static ApiException NotFound(string message = "Not found") => new(404, message);
    public static ApiException BadRequest(string message) => new(400, message);
}
