using System.Net.Http.Json;
using System.Text.Json;
using AjBoilerplate.Contracts.Common;

namespace AjBoilerplate.IntegrationTests.Support;

/// <summary>Reads the response envelope. Every endpoint returns one, so every assertion goes
/// through here rather than each test re-deriving the shape.</summary>
public static class EnvelopeAssertions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Deserializes an enveloped body and returns just the unwrapped <c>data</c>.</summary>
    public static async Task<T> ReadEnvelopeAsync<T>(this HttpContent content)
    {
        var envelope = await content.ReadFromJsonAsync<ApiResponse<T>>(Options);
        Assert.NotNull(envelope);
        Assert.True(envelope.IsSuccess, $"Expected a success envelope but got: {envelope.Message}");
        Assert.NotNull(envelope.Data);
        return envelope.Data;
    }

    /// <summary>Reads the whole envelope of a &gt;=400 response without assuming a payload shape —
    /// for asserting on <c>success</c>/<c>message</c>/<c>code</c>/<c>traceId</c> themselves.</summary>
    public static async Task<ApiResponse<object?>> ReadErrorEnvelopeAsync(this HttpContent content)
    {
        var envelope = await content.ReadFromJsonAsync<ApiResponse<object?>>(Options);
        Assert.NotNull(envelope);
        return envelope;
    }
}
