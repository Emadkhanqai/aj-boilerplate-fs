using System.Globalization;

namespace AjBoilerplate.Auth.Sessions;

public enum EmailDomainVerdict
{
    Allowed,

    /// <summary>Not one address: no single <c>@</c>, an empty part, whitespace, or a domain that is not a host name.</summary>
    InvalidFormat,

    DomainNotAllowed,

    /// <summary>The allowlist is empty, so nothing is allowed (fail closed).</summary>
    NotConfigured,
}

/// <summary>
/// Which e-mail domains may be given a NEW directory account (<c>Auth:Admin:AllowedEmailDomains</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>DENY BY DEFAULT.</b> An empty or missing list refuses every creation. The rule is applied where the account is actually
/// made, so a caller that skips its own check still cannot create an account for an external address.
/// </para>
/// <para>
/// Both sides are trimmed, lower-cased, stripped of the trailing root dot and
/// converted to ASCII (IDNA), then compared EXACTLY. A Cyrillic look-alike becomes <c>xn--...</c>
/// and matches nothing; <c>evil-example.com</c>, <c>example.com.evil.net</c> and
/// <c>sub.example.com</c> do not match <c>example.com</c>. The local part plays no part.
/// </para>
/// </remarks>
public static class EmailDomainPolicy
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    public static EmailDomainVerdict Check(string? email, IEnumerable<string>? allowedDomains)
    {
        var allowed = (allowedDomains ?? [])
            .Select(NormaliseDomain)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (allowed.Count == 0)
        {
            return EmailDomainVerdict.NotConfigured;
        }

        var domain = DomainOf(email);
        if (domain is null)
        {
            return EmailDomainVerdict.InvalidFormat;
        }

        return allowed.Contains(domain) ? EmailDomainVerdict.Allowed : EmailDomainVerdict.DomainNotAllowed;
    }

    /// <summary>The normalised domain of a single well-formed address, or null.</summary>
    public static string? DomainOf(string? email)
    {
        var value = email?.Trim();
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
        {
            return null;
        }

        return NormaliseDomain(value[(at + 1)..]);
    }

    /// <summary>Lower-case ASCII form of a host name, without the trailing root dot; null when it is not one.</summary>
    public static string? NormaliseDomain(string? domain)
    {
        var value = domain?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(value) || !value.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            // UseStd3AsciiRules refuses anything that is not letters, digits and hyphens per label, and an empty label.
            return Idn.GetAscii(value).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
