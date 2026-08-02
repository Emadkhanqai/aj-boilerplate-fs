using AjBoilerplate.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace AjBoilerplate.IntegrationTests.Support;

/// <summary>
/// Starts one throwaway SQL Server container for the whole integration suite, applies the EF Core
/// migrations to it, and hands out a <see cref="ApiFactory"/> bound to it.
///
/// The container is shared across every test class in <see cref="DatabaseCollection"/> because
/// starting SQL Server costs seconds, not milliseconds — one per class would dominate the suite's
/// runtime. xUnit runs the classes in a collection serially, so sharing one database between them
/// is safe; tests that could otherwise collide use unique names rather than relying on isolation.
///
/// Applying the migrations here rather than calling <c>EnsureCreated</c> is deliberate and is the
/// only reason the migrations are covered at all: every run proves that <c>InitialCreate</c> applies
/// cleanly to a genuinely empty database, which is exactly what a deployment does.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>How long to keep trying to connect after the container reports itself started.
    /// Generous, because a cold SQL Server image on an emulated architecture is genuinely slow.</summary>
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromSeconds(1);

    // Pinned to the same image tag docker-compose.yml uses, so local, CI, and the compose stack all
    // run the same engine. Testcontainers supplies the throwaway SA password itself — there is no
    // credential to commit, which is the main reason this suite owns its own database rather than
    // talking to a CI service container configured with a static one.
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    private ApiFactory? _api;

    /// <summary>The application host, bound to the container.</summary>
    public ApiFactory Api => _api ?? throw new InvalidOperationException("The fixture has not been initialised.");

    /// <summary>
    /// The container's connection string, with the client's address preference pinned to IPv4.
    ///
    /// Testcontainers publishes the port on <c>127.0.0.1</c>, but <c>Microsoft.Data.SqlClient</c>'s
    /// managed SNI resolves that to the IPv6 loopback first on macOS and some Linux hosts and then
    /// gives up with "the server was not found" (TCP provider, error 35) — even though the port is
    /// open and a raw socket connects. The failure looks exactly like a container that never
    /// started, which is what makes it worth pinning rather than rediscovering.
    /// </summary>
    private string ConnectionString =>
        new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            IPAddressPreference = SqlConnectionIPAddressPreference.IPv4First,
        }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _api = new ApiFactory(ConnectionString);

        try
        {
            await WaitUntilAcceptingConnectionsAsync();

            await using var scope = Api.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            // A fixture failure fails every test in the collection at once, and the raw exception
            // ("the server was not found") says nothing about WHY. The container's own log usually
            // says exactly why — out of memory, an unsupported platform, a failed EULA — so it is
            // attached here rather than left to be rediscovered by hand.
            var (stdout, stderr) = await _container.GetLogsAsync();
            throw new InvalidOperationException(
                $"Could not migrate the test database at '{ConnectionString}'.{Environment.NewLine}" +
                $"--- container stdout ---{Environment.NewLine}{stdout}{Environment.NewLine}" +
                $"--- container stderr ---{Environment.NewLine}{stderr}",
                ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// Polls until the server actually accepts a client connection, or gives up after
    /// <see cref="ReadinessTimeout"/>.
    ///
    /// Testcontainers' own readiness probe runs INSIDE the container, so it reports success as soon
    /// as the engine answers on the container's loopback — a moment before the published host port
    /// is accepting. Connecting straight after <c>StartAsync</c> therefore loses a race and fails
    /// with "connection refused", which reads exactly like a container that never started. A
    /// readiness probe is an approximation; the first real connection should not be the thing that
    /// discovers it was early.
    /// </summary>
    private async Task WaitUntilAcceptingConnectionsAsync()
    {
        var deadline = DateTimeOffset.UtcNow + ReadinessTimeout;
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (SqlException ex)
            {
                last = ex;
                await Task.Delay(ReadinessPollInterval);
            }
        }

        throw new TimeoutException(
            $"SQL Server did not accept a connection within {ReadinessTimeout.TotalSeconds:F0}s.", last);
    }

    /// <summary>
    /// Runs <paramref name="action"/> against a fresh <see cref="AppDbContext"/> scope, for seeding
    /// or for asserting on persisted state rather than on a response body.
    /// </summary>
    public async Task WithDbContextAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

/// <summary>
/// Binds every integration test class to the one shared <see cref="SqlServerFixture"/>. Without this
/// collection each class would get its own fixture — and therefore its own SQL Server container.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
