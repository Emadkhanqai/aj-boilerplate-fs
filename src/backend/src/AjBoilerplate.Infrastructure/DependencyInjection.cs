using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Features;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Infrastructure.Cloud;
using AjBoilerplate.Infrastructure.Email;
using AjBoilerplate.Infrastructure.Features;
using AjBoilerplate.Infrastructure.Messaging;
using AjBoilerplate.Infrastructure.Persistence;
using AjBoilerplate.Infrastructure.Secrets;
using AjBoilerplate.Infrastructure.Security;
using AjBoilerplate.Infrastructure.Storage;
using AjBoilerplate.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AjBoilerplate.Infrastructure;

/// <summary>Composition root for the Infrastructure layer (SQL Server, EF Core, cache, transports,
/// cloud secrets).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        AddPersistence(services, configuration, environment);
        AddCache(services, configuration);
        AddCloud(services, configuration);
        AddStorage(services, configuration, environment);
        AddTransports(services, configuration);

        services.AddSingleton<IClock, SystemClock>();

        // Configuration-backed feature flags. A singleton reading IConfiguration on each call, so a
        // provider that reloads on change makes a flag flippable without a redeploy — see
        // ConfigurationFeatureFlags.
        services.AddSingleton<IFeatureFlags, ConfigurationFeatureFlags>();

        // Opaque-id obfuscation: stateless over a derived key/IV, so a singleton is safe and avoids
        // re-deriving the key from HKDF on every request.
        services.Configure<EntityIdObfuscationOptions>(configuration.GetSection(EntityIdObfuscationOptions.SectionName));
        services.AddSingleton<IEntityIdCodec, AesEntityIdCodec>();

        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

        services.AddScoped<IFeatureAnnouncementRepository, FeatureAnnouncementRepository>();

        // SAMPLE SLICE — delete with the rest of the Item sample.
        services.AddScoped<IItemRepository, ItemRepository>();

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // The localhost convenience string below is for a developer's own machine. Outside
            // Development it must never be a silent stand-in for a real connection string that the
            // secret store or the environment failed to supply — that string does not even point at
            // a reachable server in any deployed environment, so the failure would surface as a
            // confusing runtime error long after a bad deploy went live.
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "ConnectionStrings:Default is not configured. Every deployed environment must supply " +
                    "it (cloud secret store or environment) — refusing to fall back to a localhost " +
                    "connection string outside Development.");
            }

            connectionString = "Server=localhost;Database=AppDb;Trusted_Connection=True;TrustServerCertificate=True;";
        }

        // SQL Server. The schema is owned by EF Core migrations — never EnsureCreated, never manual DDL.
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));
    }

    private static void AddCache(IServiceCollection services, IConfiguration configuration)
    {
        // NO CLOUD BRANCH HERE, deliberately: Memorystore for Redis (GCP) and Azure Cache for Redis
        // speak the same wire protocol, so one connection string and one registration serve both.
        // The difference lives entirely in infra/.
        //
        // An unconfigured Redis silently degrades every cache-backed read to a process-local,
        // non-shared cache — safe on a single instance, quietly wrong the moment there is more than
        // one. Program.cs re-checks this same key at startup and logs a warning outside Development,
        // because the DI container has not been built here yet and there is no logger to use.
        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis))
        {
            services.AddDistributedMemoryCache();
        }
        else
        {
            services.AddStackExchangeRedisCache(options => options.Configuration = redis);
        }
    }

    /// <summary>
    /// The CLOUD_PROVIDER switch. Exactly one pair of registrations differs between clouds — the
    /// secrets provider here, and the authentication issuer in <c>AddApiAuthentication</c>.
    /// </summary>
    private static void AddCloud(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CloudOptions>(configuration.GetSection(CloudOptions.SectionName));

        var cloud = configuration.GetSection(CloudOptions.SectionName).Get<CloudOptions>() ?? new CloudOptions();

        // Resolve() throws on an unrecognised value, so a typo in CLOUD_PROVIDER fails at startup
        // rather than silently selecting a default cloud.
        switch (cloud.Resolve())
        {
            case CloudProvider.Azure when !string.IsNullOrWhiteSpace(cloud.Azure.KeyVaultUri):
                services.AddSingleton<ISecretsProvider, AzureKeyVaultSecretsProvider>();
                break;

            case CloudProvider.Gcp when !string.IsNullOrWhiteSpace(cloud.Gcp.ProjectId):
                services.AddSingleton<ISecretsProvider, GcpSecretManagerSecretsProvider>();
                break;

            default:
                // The provider is selected but its store is unconfigured — local dev, tests, offline.
                services.AddSingleton<ISecretsProvider, NullSecretsProvider>();
                break;
        }
    }

    /// <summary>
    /// File storage, following the same <c>CLOUD_PROVIDER</c> switch as <see cref="AddCloud"/>.
    ///
    /// <para>
    /// <b>THE CLOUD ARMS ARE NOT IMPLEMENTED, AND THEY FAIL LOUDLY RATHER THAN QUIETLY.</b> Shipping a
    /// Cloud Storage or Blob Storage implementation would mean adding a heavy provider SDK to every
    /// consuming project — including the ones that store no files at all — and an untested
    /// implementation nobody has run against a real bucket is worse than none: it looks finished.
    /// A no-op that silently discarded uploads would be worse still.
    /// </para>
    ///
    /// <para>
    /// So: configuring a bucket or a container is read as a statement of intent this boilerplate
    /// cannot honour, and it throws at STARTUP with the exact steps to fix it — the same discipline
    /// <c>CloudOptions.Resolve()</c> applies to an unrecognised provider, and for the same reason. The
    /// alternative (falling back to local storage) would put files on a container filesystem that
    /// disappears on the next deploy, which is a data-loss bug discovered long after the fact.
    /// </para>
    ///
    /// <para>
    /// With no bucket or container configured, <see cref="LocalFileStorage"/> is registered — but only
    /// in Development, because a filesystem is process-local and would silently stop working the
    /// moment a deployed environment runs more than one instance.
    /// </para>
    /// </summary>
    private static void AddStorage(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));

        var storage = configuration.GetSection(FileStorageOptions.SectionName).Get<FileStorageOptions>() ?? new FileStorageOptions();
        var cloud = configuration.GetSection(CloudOptions.SectionName).Get<CloudOptions>() ?? new CloudOptions();

        var (configured, setting, guidance) = cloud.Resolve() switch
        {
            CloudProvider.Azure => (
                !string.IsNullOrWhiteSpace(storage.Azure.Container),
                "Storage:Azure:Container",
                "Add Azure.Storage.Blobs to AjBoilerplate.Infrastructure, implement IFileStorage over BlobContainerClient "
                    + "(authenticating with DefaultAzureCredential, as AzureKeyVaultSecretsProvider does), and register it here."),
            _ => (
                !string.IsNullOrWhiteSpace(storage.Gcp.Bucket),
                "Storage:Gcp:Bucket",
                "Add Google.Cloud.Storage.V1 to AjBoilerplate.Infrastructure, implement IFileStorage over StorageClient "
                    + "(authenticating with Application Default Credentials, as GcpSecretManagerSecretsProvider does), and register it here."),
        };

        if (configured)
        {
            // Intent stated and not honourable. Failing at STARTUP is right here specifically because
            // the operator has asked for cloud storage: booting anyway would mean either discarding
            // files or writing them to a container filesystem that vanishes on the next deploy, and
            // both are discovered long after the data is gone.
            throw new InvalidOperationException(
                $"{setting} is configured, but this boilerplate ships no cloud file-storage implementation. "
                + guidance
                + " Refusing to start rather than falling back to local disk, which would lose every file on the next deploy.");
        }

        if (environment.IsDevelopment())
        {
            services.AddSingleton<IFileStorage, LocalFileStorage>();
            return;
        }

        // Nothing configured, outside Development. This must NOT throw at startup: most applications
        // built on this boilerplate store no files at all, and refusing to boot over an unused
        // optional dependency would be a worse default than the problem it guards against. So the
        // failure is deferred to first use — an application that never touches IFileStorage runs
        // normally, and one that does gets an immediate, actionable error instead of writing to a
        // disk that disappears.
        services.AddSingleton<IFileStorage>(_ => new UnconfiguredFileStorage(setting, guidance));
    }

    private static void AddTransports(IServiceCollection services, IConfiguration configuration)
    {
        // Email: real SMTP when a host is configured, a logging no-op otherwise.
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        if (string.IsNullOrWhiteSpace(configuration["Smtp:Host"]))
        {
            services.AddScoped<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }

        // Integration events: a logging no-op until a real broker is wired up behind the same port.
        services.AddScoped<IIntegrationEventPublisher, LoggingIntegrationEventPublisher>();
    }
}
