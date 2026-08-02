namespace AjBoilerplate.Application.Abstractions;

/// <summary>
/// Reads a secret from the selected cloud's secret store at RUNTIME, by logical name.
///
/// This is the second half of the secrets story and is deliberately separate from the boot-time
/// configuration sources in <c>AjBoilerplate.Api/Configuration</c>. Those load the whole secret set
/// once, into <c>IConfiguration</c>, before the host starts — the right shape for connection
/// strings and signing keys that must exist for the app to run at all. This port covers the other
/// case: a secret that must be fetched fresh (a rotated third-party API key, a per-tenant
/// credential) without a restart, and it is the only secrets surface an Application-layer service
/// may depend on.
///
/// Which implementation is registered is chosen by <c>CLOUD_PROVIDER</c> (<c>Cloud:Provider</c>) —
/// Google Cloud Secret Manager for <c>gcp</c>, Azure Key Vault for <c>azure</c>.
/// </summary>
public interface ISecretsProvider
{
    /// <summary>
    /// The current value of <paramref name="name"/>, or <c>null</c> when the store holds no such
    /// secret. A missing secret is a foreseeable, non-exceptional state (an optional integration
    /// that was never configured), so it must surface as this null rather than as a
    /// provider-specific exception. Genuine failures — an unreachable store, a denied permission —
    /// still throw.
    /// </summary>
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken);
}
