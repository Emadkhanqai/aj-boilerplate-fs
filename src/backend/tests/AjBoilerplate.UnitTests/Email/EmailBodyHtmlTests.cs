using AjBoilerplate.Infrastructure.Email;

namespace AjBoilerplate.UnitTests.Email;

/// <summary>
/// Plain-text-to-HTML conversion for outbound email. The escaping order is the whole point: encode
/// first, then link — reversing it turns a caller-supplied string into an HTML injection vector in
/// every recipient's inbox.
/// </summary>
public sealed class EmailBodyHtmlTests
{
    [Fact]
    public void Markup_in_the_body_is_escaped()
    {
        var html = EmailBodyHtml.From("<script>alert('x')</script>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_url_becomes_a_link() =>
        Assert.Contains(
            "<a href=\"https://example.invalid/reset\">https://example.invalid/reset</a>",
            EmailBodyHtml.From("Open https://example.invalid/reset to continue."),
            StringComparison.Ordinal);

    [Fact]
    public void Newlines_become_line_breaks() =>
        Assert.Contains("Line one<br />Line two", EmailBodyHtml.From("Line one\nLine two"), StringComparison.Ordinal);

    [Fact]
    public void Windows_newlines_produce_a_single_break() =>
        Assert.DoesNotContain("<br /><br />", EmailBodyHtml.From("Line one\r\nLine two"), StringComparison.Ordinal);

    [Fact]
    public void An_empty_body_produces_an_empty_string() =>
        Assert.Equal(string.Empty, EmailBodyHtml.From(""));

    [Fact]
    public void An_anchor_smuggled_into_the_text_is_not_rendered_as_markup()
    {
        var html = EmailBodyHtml.From("<a href=\"https://evil.invalid\">click</a>");

        // The URL inside the escaped attribute must not be resurrected into a real anchor.
        Assert.DoesNotContain("<a href=\"https://evil.invalid\">click</a>", html, StringComparison.Ordinal);
    }
}
