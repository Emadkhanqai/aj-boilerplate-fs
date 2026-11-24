using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using StackExchange.Redis;

namespace AjBoilerplate.Auth.Redis;

public static class RedisConnectionOptionsFactory
{
    public static ConfigurationOptions Create(string connectionString, string? caCertPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = ConfigurationOptions.Parse(connectionString);

        options.AbortOnConnectFail = false;

        var validator = CreateValidator(options.Ssl, caCertPem);
        if (validator is not null)
        {
            options.CertificateValidation += validator;
        }

        return options;
    }

    public static RemoteCertificateValidationCallback? CreateValidator(bool ssl, string? caCertPem)
    {
        if (!ssl || string.IsNullOrWhiteSpace(caCertPem))
        {
            return null;
        }

        var trustedRoot = X509Certificate2.CreateFromPem(caCertPem);
        return (_, certificate, _, errors) => IsTrusted(certificate, errors, trustedRoot);
    }

    public static bool IsTrusted(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 trustedRoot)
    {
        ArgumentNullException.ThrowIfNull(trustedRoot);

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null)
        {
            return false;
        }

        X509Certificate2? copy = null;
        var leaf = certificate as X509Certificate2
            ?? (copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));

        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            return chain.Build(leaf);
        }
        finally
        {
            copy?.Dispose();
        }
    }
}
