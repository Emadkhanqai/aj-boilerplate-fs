namespace AjBoilerplate.Auth.Identity;

public static class LoginHints
{
    public const int MaxLength = 254;

    private const string Forbidden = ",;<>\"\\";

    public static string? Normalize(string? value)
    {
        var hint = value?.Trim();
        if (string.IsNullOrEmpty(hint) || hint.Length > MaxLength)
        {
            return null;
        }

        foreach (var c in hint)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || Forbidden.Contains(c, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var at = hint.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != hint.LastIndexOf('@'))
        {
            return null;
        }

        var domain = hint.AsSpan(at + 1);
        return domain.Length >= 3 && domain[0] != '.' && domain[^1] != '.' && domain.Contains('.') ? hint : null;
    }
}
