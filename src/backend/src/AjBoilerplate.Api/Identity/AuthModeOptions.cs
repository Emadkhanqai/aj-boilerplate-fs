namespace AjBoilerplate.Api.Identity;

/// <summary>
/// Binding for the <c>Auth:Mode</c> config value. The only mode is <c>gateway</c>, and a blank value
/// means gateway: the API sits behind the nginx gateway, which authenticates the browser's session
/// and forwards a Keycloak JWT as a bearer token plus the <c>X-Usp-Session-*</c> headers.
///
/// <para>
/// Any other value fails at startup rather than being read as "something else, so relax". A typo here
/// must not be able to select a weaker mode that does not exist. Today the value is validated and nothing
/// more; it is the seam a future second mode would branch on.
/// </para>
/// </summary>
public sealed class AuthModeOptions
{
    public const string SectionName = "Auth";

    public const string Gateway = "gateway";

    public string Mode { get; init; } = string.Empty;

    /// <summary>True for <c>gateway</c> and for a blank value (gateway is the default). False means an
    /// unrecognised mode, which <see cref="AuthenticationSetup"/> refuses to start with.</summary>
    public bool IsGateway =>
        string.IsNullOrWhiteSpace(Mode) || string.Equals(Mode.Trim(), Gateway, StringComparison.OrdinalIgnoreCase);
}
