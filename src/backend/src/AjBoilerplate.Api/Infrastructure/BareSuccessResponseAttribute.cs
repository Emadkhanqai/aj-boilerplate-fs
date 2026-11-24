namespace AjBoilerplate.Api.Infrastructure;

/// <summary>
/// Marks an action whose SUCCESS body is sent bare, not wrapped in the <c>ApiResponse</c> envelope — for a
/// machine caller that reads a field at the top level of the body (the Auth service reads <c>outcome</c> of the
/// session-ended receiver). Errors are still enveloped. Honoured by <see cref="EnvelopeResultFilter"/> and by
/// <see cref="EnvelopeResponseSchemaFilter"/>, so the document says what the wire does.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BareSuccessResponseAttribute : Attribute;
