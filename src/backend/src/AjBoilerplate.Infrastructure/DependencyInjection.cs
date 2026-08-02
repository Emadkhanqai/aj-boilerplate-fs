using AjBoilerplate.Application.Abstractions;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Infrastructure.Cloud;
using AjBoilerplate.Infrastructure.Email;
using AjBoilerplate.Infrastructure.Messaging;
using AjBoilerplate.Infrastructure.Persistence;
using AjBoilerplate.Infrastructure.Secrets;
using AjBoilerplate.Infrastructure.Security;
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
        AddTransports(services, configuration);

        services.AddSingleton<IClock, SystemClock>();

        // Opaque-id obfuscation: stateless over a derived key/IV, so a singleton is safe and avoids
        // re-deriving the key from HKDF on every request.
        services.Configure<EntityIdObfuscationOptions>(configuration.GetSection(EntityIdObfuscationOptions.SectionName));
        services.AddSingleton<IEntityIdCodec, AesEntityIdCodec>();

        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();

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
