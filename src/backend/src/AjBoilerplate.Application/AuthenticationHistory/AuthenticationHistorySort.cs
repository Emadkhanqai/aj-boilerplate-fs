namespace AjBoilerplate.Application.AuthenticationHistory;

/// <summary>The columns the sign-in history can be sorted on — every data column it shows.</summary>
public enum AuthenticationHistorySortField
{
    SignedIn,
    User,
    SessionEnded,
    Duration,
    Result,
    EndReason,
}

public static class AuthenticationHistorySort
{
    /// <summary>The wire names of <see cref="AuthenticationHistorySortField"/>, in declaration order.</summary>
    public static readonly IReadOnlyList<string> Keys = ["signedIn", "user", "sessionEnded", "duration", "result", "endReason"];

    /// <summary>
    /// Both omitted: signed-in, newest first. <c>sortBy</c> alone: ascending. An unknown key or direction is
    /// false (a 400), never a silent fallback — a fallback would show an order the caller did not ask for.
    /// </summary>
    public static bool TryParse(string? sortBy, string? sortDir, out AuthenticationHistorySortField field, out bool descending)
    {
        field = AuthenticationHistorySortField.SignedIn;
        descending = true;
        if (string.IsNullOrWhiteSpace(sortBy) && string.IsNullOrWhiteSpace(sortDir))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            var index = Keys.ToList().FindIndex(k => string.Equals(k, sortBy.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            field = (AuthenticationHistorySortField)index;
            descending = false;
        }

        if (string.IsNullOrWhiteSpace(sortDir))
        {
            return true;
        }

        switch (sortDir.Trim().ToLowerInvariant())
        {
            case "asc":
                descending = false;
                return true;
            case "desc":
                descending = true;
                return true;
            default:
                return false;
        }
    }
}
