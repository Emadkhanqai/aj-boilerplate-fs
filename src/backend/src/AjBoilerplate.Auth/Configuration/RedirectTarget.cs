namespace AjBoilerplate.Auth.Configuration;

public static class RedirectTarget
{
    public const string Default = "/";

    public const int MaxLength = 1024;

    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value[0] != '/')
        {
            return false;
        }

        if (value.Length > 1 && (value[1] == '/' || value[1] == '\\'))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c == '\\' || char.IsControl(c))
            {
                return false;
            }
        }

        return !Uri.TryCreate(value, UriKind.Absolute, out var absolute) || absolute.Scheme == Uri.UriSchemeFile;
    }

    public static string OrDefault(string? value) => IsSafe(value) ? value! : Default;
}
