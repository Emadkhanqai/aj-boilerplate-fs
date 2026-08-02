namespace AjBoilerplate.Domain.Common;

/// <summary>
/// Raised when a domain invariant is violated. Persistence-ignorant; no infrastructure concerns.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
