using System.Text.Json.Serialization;

namespace AjBoilerplate.Contracts.Common;

/// <summary>Uniform success/error envelope wrapping a typed payload. Every API response uses this
/// shape (or the non-generic form for responses with no payload).</summary>
public class ApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool IsSuccess { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("errors")]
    public List<string>? Errors { get; set; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; } = 200;

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    public static ApiResponse<T> Success(T data, string? message = null, int statusCode = 200) =>
        new() { IsSuccess = true, Data = data, Message = message, StatusCode = statusCode };

    public static ApiResponse<T> Failure(string message, int statusCode = 400, string? code = null, List<string>? errors = null) =>
        new() { IsSuccess = false, Message = message, Code = code, Errors = errors, StatusCode = statusCode };
}

/// <summary>Non-generic envelope for responses with no payload (errors, 200-with-no-body successes).</summary>
public class ApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("errors")]
    public List<string>? Errors { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    public static ApiResponse CreateSuccess(string? message = null) => new() { Success = true, Message = message };

    public static ApiResponse CreateError(string message, string? code = null, List<string>? errors = null) =>
        new() { Success = false, Message = message, Code = code, Errors = errors };
}
