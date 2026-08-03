using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AjBoilerplate.IntegrationTests.Support;

/// <summary>
/// The integration-test host. It boots the REAL application — the real middleware order, the real
/// exception-handler chain, the real envelope filter, the real authorization policies, and the real
/// SQL Server registration inside <c>AddInfrastructure</c> — and swaps exactly two things:
///
/// <list type="number">
///   <item>The authentication scheme, for <see cref="TestAuthHandler"/>, so tests can act as a role
///     without a live identity provider.</item>
///   <item>The connection string, pointed at the throwaway SQL Server container
///     <see cref="SqlServerFixture"/> owns.</item>
/// </list>
///
/// Note what is NOT swapped: the database provider. Overriding a configuration value rather than
/// re-registering the <c>DbContext</c> means these tests exercise the same <c>AddInfrastructure</c>
/// code path production does — <c>UseSqlServer</c>, the migrations-assembly wiring, and the real SQL
/// EF Core generates, against a real engine that actually enforces its constraints.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The connection string is supplied as an ENVIRONMENT VARIABLE, not through
    /// <c>ConfigureAppConfiguration</c>.
    ///
    /// That is not a stylistic choice. <c>Program.cs</c> passes <c>builder.Configuration</c> to
    /// <c>AddInfrastructure</c>, which reads <c>ConnectionStrings:Default</c> immediately, during
    /// service registration — before <c>builder.Build()</c>, which is when a test host's
    /// <c>ConfigureAppConfiguration</c> callbacks are applied. An override added there is therefore
    /// too late, and the host silently keeps the appsettings value, producing a connection refused
    /// against localhost that looks like a broken container.
    ///
    /// An environment variable is read by <c>WebApplication.CreateBuilder</c> up front, so it wins.
    /// It is also exactly how a container or a cloud runtime supplies this value, which means these
    /// tests exercise the real configuration path rather than a test-only seam.
    /// </summary>
    public ApiFactory(string connectionString)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", connectionString);
        Environment.SetEnvironmentVariable(
            "RateLimiting__PermitLimit", TestPermitLimit.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// How many requests the test host allows per rate-limit window.
    ///
    /// The production default is 100 per 60 seconds, partitioned by <c>User.Identity.Name</c> — and
    /// <see cref="TestAuthHandler"/> gives every authenticated test caller the same <c>name</c>
    /// claim, so THE ENTIRE SUITE SHARES ONE PARTITION. The whole collection runs well inside a
    /// single 60-second window, so at the production limit the suite eventually 429s itself, and the
    /// failure lands on whichever unrelated test happened to be running when the quota ran out. That
    /// is a property of how this suite authenticates, not of the code under test.
    ///
    /// Raised here rather than by weakening the shipped default: the limiter that ships must stay the
    /// one that ships. Nothing asserts on 429, so nothing is lost by lifting it for the test host — if
    /// you ever DO write a rate-limit test, give it its own factory with a low limit rather than
    /// lowering this one back down.
    /// </summary>
    private const int TestPermitLimit = 10_000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development so appsettings.Development.json supplies the placeholder secrets the host
        // expects; its connection string loses to the environment variable set above.
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
    }

    /// <summary>A client whose requests carry the given role.</summary>
    public HttpClient CreateClientAs(string role)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        return client;
    }

    /// <summary>A client whose requests carry the given role AND identify as a specific person —
    /// for the per-user behaviour that two clients holding the same role cannot demonstrate.</summary>
    public HttpClient CreateClientAs(string role, string userId)
    {
        var client = CreateClientAs(role);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        return client;
    }
}
