namespace AjBoilerplate.Auth.Redis;

internal static class RedisKeys
{
    public const string Prefix = "auth:";

    public static string Session(string sessionKey) => $"{Prefix}sess:{sessionKey}";

    public static string SubjectIndex(string subject) => $"{Prefix}user:{subject}";

    public static string Login(string state) => $"{Prefix}login:{state}";

    public static string Handoff(string ticketKey) => $"{Prefix}handoff:{ticketKey}";

    public static string Lock(string key) => $"{Prefix}lock:{key}";

    public static string Revoked(string sessionKey) => $"{Prefix}revoked:{sessionKey}";

}

internal static class SessionFields
{
    public const string Jwt = "jwt";
    public const string JwtExp = "jwt_exp";
    public const string RefreshToken = "rt";
    public const string Subject = "sub";
    public const string ClientId = "client";
    public const string ForceRefresh = "force_refresh";
    public const string CreatedAt = "created";
    public const string LastSeen = "last_seen";

    /// <summary>Storage key of the session whose refresh token this one renews from; empty for a session that holds its own.</summary>
    public const string RefreshVia = "via";
}
