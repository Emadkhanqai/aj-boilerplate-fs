namespace AjBoilerplate.Application.Abstractions;

/// <summary>
/// Whether a named feature is switched on.
///
/// Deliberately one method. A flag provider's whole job is to answer that question, and every richer
/// surface a real flag platform offers — percentage rollouts, per-user targeting, experiment
/// variants — is a different question wearing the same name. Keeping this port narrow is what lets it
/// be re-implemented over LaunchDarkly, Unleash, Azure App Configuration, or a database table without
/// changing a single call site; widening it to match one vendor's model would tie every consumer to
/// that vendor's semantics.
///
/// <para>
/// <b>A flag is not an authorization check.</b> Whether a caller MAY do something is
/// <c>RoleCapabilities</c> and the authorization policies; whether the feature EXISTS in this
/// deployment is this. Gating a permission behind a flag puts a security decision in a configuration
/// file that operations can flip.
/// </para>
///
/// <para>
/// <b>Flags are meant to be removed.</b> Every one is a branch that must be tested both ways. Add one
/// with a plan for deleting it, and delete it once the feature has shipped everywhere — a flag that
/// has read <c>true</c> in production for a year is not a flag, it is dead configuration and a live
/// untested code path.
/// </para>
/// </summary>
public interface IFeatureFlags
{
    /// <summary>
    /// True when <paramref name="name"/> is switched on. An unknown flag is OFF: a typo, a flag that
    /// has not been configured in this environment yet, and a flag someone deleted all resolve to
    /// "the feature is not available", which is the safe direction to be wrong in.
    /// </summary>
    bool IsEnabled(string name);
}
