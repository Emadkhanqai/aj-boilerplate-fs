using AjBoilerplate.Api.Observability;

namespace AjBoilerplate.UnitTests.Observability;

/// <summary>
/// Credential redaction for request logging. This is the last line of defence against a token in a
/// URL being written verbatim into the application log on every request — a leak that is completely
/// invisible from the outside, because the response looks perfectly normal.
/// </summary>
public sealed class RequestLogSanitizerTests
{
    [Fact]
    public void A_path_with_no_query_is_unchanged() =>
        Assert.Equal("/api/v1/items", RequestLogSanitizer.Sanitize("/api/v1/items"));

    [Fact]
    public void An_ordinary_query_parameter_is_kept() =>
        // The log has to stay useful — redacting everything is as bad as redacting nothing.
        Assert.Equal("/api/v1/items?page=2&search=widget", RequestLogSanitizer.Sanitize("/api/v1/items?page=2&search=widget"));

    [Theory]
    [InlineData("access_token")]
    [InlineData("token")]
    [InlineData("code")]
    [InlineData("api_key")]
    [InlineData("password")]
    [InlineData("signature")]
    public void A_sensitive_query_value_is_redacted(string key)
    {
        var sanitized = RequestLogSanitizer.Sanitize($"/api/v1/items?{key}=super-secret-value");

        Assert.DoesNotContain("super-secret-value", sanitized, StringComparison.Ordinal);
        Assert.Contains(RequestLogSanitizer.Redacted, sanitized, StringComparison.Ordinal);
        // The parameter NAME survives, so an operator can still see which credential was presented.
        Assert.Contains(key, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_is_case_insensitive_on_the_parameter_name() =>
        Assert.DoesNotContain("abc", RequestLogSanitizer.Sanitize("/callback?CODE=abc"), StringComparison.Ordinal);

    [Fact]
    public void Only_the_sensitive_value_is_redacted_when_parameters_are_mixed()
    {
        var sanitized = RequestLogSanitizer.Sanitize("/api/v1/items?page=2&token=secret&search=widget");

        Assert.Contains("page=2", sanitized, StringComparison.Ordinal);
        Assert.Contains("search=widget", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_whose_name_merely_ends_in_a_sensitive_word_is_not_redacted() =>
        // "sort_code" is not "code". Over-matching here would quietly destroy legitimate log data.
        Assert.Contains("abc", RequestLogSanitizer.Sanitize("/api/v1/items?sort_code=abc"), StringComparison.Ordinal);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_null_or_empty_path_yields_an_empty_string(string? path) =>
        Assert.Equal(string.Empty, RequestLogSanitizer.Sanitize(path));

    [Fact]
    public void RedactSegmentAfter_redacts_the_credential_segment_and_keeps_the_rest() =>
        Assert.Equal(
            $"/api/v1/invitations/{RequestLogSanitizer.Redacted}/accept",
            RequestLogSanitizer.RedactSegmentAfter("/api/v1/invitations/abc123/accept", "/api/v1/invitations"));

    [Fact]
    public void RedactSegmentAfter_handles_the_credential_being_the_last_segment() =>
        Assert.Equal(
            $"/api/v1/invitations/{RequestLogSanitizer.Redacted}",
            RequestLogSanitizer.RedactSegmentAfter("/api/v1/invitations/abc123", "/api/v1/invitations"));

    [Fact]
    public void RedactSegmentAfter_leaves_every_other_route_untouched() =>
        Assert.Equal(
            "/api/v1/items/42",
            RequestLogSanitizer.RedactSegmentAfter("/api/v1/items/42", "/api/v1/invitations"));
}
