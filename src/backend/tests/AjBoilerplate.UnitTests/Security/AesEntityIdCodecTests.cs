using AjBoilerplate.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.UnitTests.Security;

/// <summary>Opaque entity-id tokens: deterministic, reversible, and tamper-evident.</summary>
public sealed class AesEntityIdCodecTests
{
    private static AesEntityIdCodec Codec(string secret = "unit-test-only-entity-id-obfuscation-secret") =>
        new(Options.Create(new EntityIdObfuscationOptions { EncryptionKey = secret }));

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    [InlineData(0)]
    public void An_id_round_trips(int id)
    {
        var codec = Codec();

        Assert.True(codec.TryDecode(codec.Encode(id), out var decoded));
        Assert.Equal(id, decoded);
    }

    [Fact]
    public void Encoding_is_deterministic() =>
        // A bookmarked URL for the same record must keep working and keep looking the same.
        Assert.Equal(Codec().Encode(42), Codec().Encode(42));

    [Fact]
    public void Different_ids_produce_different_tokens() =>
        Assert.NotEqual(Codec().Encode(42), Codec().Encode(43));

    [Fact]
    public void Consecutive_ids_do_not_produce_similar_tokens()
    {
        var codec = Codec();

        // The whole point is that a caller cannot walk the id space by nudging the token.
        Assert.NotEqual(codec.Encode(1)[..8], codec.Encode(2)[..8]);
    }

    [Fact]
    public void A_token_is_url_safe() =>
        // It travels in path segments; '+', '/', and '=' would all need escaping.
        Assert.DoesNotContain(Codec().Encode(12345), c => c is '+' or '/' or '=');

    [Fact]
    public void A_token_from_a_different_key_does_not_decode() =>
        Assert.False(Codec("another-secret-entirely").TryDecode(Codec().Encode(42), out _));

    [Fact]
    public void A_tampered_token_is_rejected()
    {
        var codec = Codec();
        var token = codec.Encode(42);
        var tampered = (token[0] == 'A' ? 'B' : 'A') + token[1..];

        Assert.False(codec.TryDecode(tampered, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ")]
    public void Garbage_is_rejected_without_throwing(string token) =>
        // A malformed token must produce a clean 400/404, never a 500.
        Assert.False(Codec().TryDecode(token, out _));

    [Fact]
    public void An_unconfigured_key_fails_at_construction()
    {
        // Loud at startup, not on whichever request first happened to need a token.
        var error = Assert.Throws<InvalidOperationException>(() => Codec(secret: "   "));

        Assert.Contains("EntityIdObfuscation:EncryptionKey", error.Message, StringComparison.Ordinal);
    }
}
