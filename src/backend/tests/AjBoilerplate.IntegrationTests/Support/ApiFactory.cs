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
    public ApiFactory(string connectionString) =>
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", connectionString);

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
}
