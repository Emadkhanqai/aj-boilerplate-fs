namespace AjBoilerplate.Application.Abstractions;

/// <summary>
/// Reversible, deterministic, keyed encoding of an internal <c>int</c> database primary key into an
/// opaque wire-safe token, and back — so a raw sequential integer id never crosses the API boundary.
/// Predictable/enumerable ids in URLs and JSON responses let a caller probe for records it should
/// not see, and leak roughly how many rows exist and when they were created, even when
/// authorization ultimately blocks the read.
///
/// Deterministic by design: the same <paramref name="id"/> always <see cref="Encode"/>s to the same
/// token, so a shared/bookmarked URL for the same record always looks identical, and the token can
/// safely be indexed/logged/compared. This is courtesy obfuscation, not a substitute for
/// server-side authorization — every caller must still pass the normal role/scope/ownership checks
/// on the decoded id.
///
/// The sample <c>Item</c> slice uses a <see cref="Guid"/> primary key and therefore does not need
/// this at all. It ships for the common case where a consuming project introduces an
/// <c>int</c>-keyed aggregate and wants opaque ids without reworking its schema.
/// </summary>
public interface IEntityIdCodec
{
    /// <summary>Encodes <paramref name="id"/> into its opaque wire token.</summary>
    string Encode(int id);

    /// <summary>
    /// Attempts to decode <paramref name="token"/> back into the original id. Returns <c>false</c> —
    /// never throws — for a malformed, tampered, or otherwise invalid token, so callers can surface a
    /// clean 400/404 instead of a 500 for garbage input.
    /// </summary>
    bool TryDecode(string token, out int id);
}
