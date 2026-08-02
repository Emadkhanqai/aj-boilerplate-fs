using AjBoilerplate.Api.Identity;

namespace AjBoilerplate.UnitTests.Identity;

/// <summary>
/// The <c>azp</c> check. A shared authorization realm issues tokens to more than one application, so
/// a token can have a valid issuer AND a valid signature and still belong to a different client —
/// accepting it would let another application's users straight in.
/// </summary>
public sealed class KeycloakAuthenticationEventsTests
{
    [Fact]
    public void An_exactly_matching_azp_is_allowed() =>
        Assert.True(KeycloakAuthenticationEvents.IsAllowedAzp("my-api", "my-api"));

    [Fact]
    public void A_different_clients_token_is_rejected() =>
        Assert.False(KeycloakAuthenticationEvents.IsAllowedAzp("other-app", "my-api"));

    [Fact]
    public void The_match_is_case_sensitive() =>
        // azp is a client identifier, never something to fuzzy-match.
        Assert.False(KeycloakAuthenticationEvents.IsAllowedAzp("My-Api", "my-api"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_azp_is_rejected(string? azp) =>
        Assert.False(KeycloakAuthenticationEvents.IsAllowedAzp(azp, "my-api"));

    [Fact]
    public void An_unconfigured_client_id_rejects_everything() =>
        // Fails CLOSED. The dangerous alternative — treating "no expected client" as "any client is
        // fine" — would silently disable this check in any environment that forgot to set it.
        Assert.False(KeycloakAuthenticationEvents.IsAllowedAzp("my-api", ""));
}
