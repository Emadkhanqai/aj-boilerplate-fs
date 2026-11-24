using AjBoilerplate.Auth.Security;

namespace AjBoilerplate.Auth.Tests;

public sealed class SessionTokenProtectorTests
{
    private static SessionTokenProtector Protector() => new(SessionTokenProtector.GenerateKey());

    [Fact]
    public void Round_trips_a_token()
    {
        var protector = Protector();
        const string Jwt = "eyJhbGciOiJSUzI1NiJ9.payload.signature";

        Assert.Equal(Jwt, protector.Unprotect(protector.Protect(Jwt)));
    }

    [Fact]
    public void Null_stays_null()
    {
        var protector = Protector();

        Assert.Null(protector.Protect(null));
        Assert.Null(protector.Unprotect(null));
    }

    [Fact]
    public void Ciphertext_is_not_the_plaintext_and_is_not_deterministic()
    {
        var protector = Protector();

        var a = protector.Protect("secret-token")!;
        var b = protector.Protect("secret-token")!;

        Assert.DoesNotContain("secret-token", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);   // a fresh nonce every time
    }

    [Fact]
    public void A_tampered_value_does_not_decrypt()
    {
        var protector = Protector();
        var value = protector.Protect("secret-token")!;

        var tampered = value[..^2] + (value[^2] == 'A' ? 'B' : 'A') + value[^1];

        Assert.Null(protector.Unprotect(tampered));
    }

    [Fact]
    public void A_value_written_under_another_key_reads_as_no_session()
    {
        var value = Protector().Protect("secret-token");

        Assert.Null(Protector().Unprotect(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")]   // valid base64, wrong length
    public void A_key_that_cannot_work_stops_start_up(string key) =>
        Assert.Throws<InvalidOperationException>(() => new SessionTokenProtector(key));
}
