namespace AjBoilerplate.Auth.Sql;

internal sealed class SessionRow
{
    public string SessionKey { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    /// <summary>AES-256-GCM protected.</summary>
    public string KeycloakJwt { get; set; } = string.Empty;

    public DateTimeOffset JwtExpiresAt { get; set; }

    /// <summary>AES-256-GCM protected.</summary>
    public string? EntraRefreshToken { get; set; }

    public bool ForceRefresh { get; set; }

    /// <summary>Sign-in time; the absolute lifetime is measured from it.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Storage key of the session whose identity-provider refresh token this one renews from; <c>null</c> for a session that holds
    /// its own. Set only by a portal hand-off. Not a foreign key, deliberately: the row it names can be signed out or swept, and a
    /// derived session then ends at its next refresh rather than blocking that delete.
    /// </summary>
    public string? RefreshVia { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>End of the sliding idle window.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
