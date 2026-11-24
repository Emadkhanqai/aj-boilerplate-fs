using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Endpoints;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AjBoilerplate.Auth.Tests;

/// <summary>The real endpoints on a real Kestrel port, composed over in-memory stores and fake upstreams.</summary>
internal sealed class EndpointHost : IAsyncDisposable
{
    public const string Subject = "kc-user";

    private WebApplication? _app;

    public InMemorySessionStore Sessions { get; } = new();

    public InMemoryLoginStore Logins { get; } = new();

    public InMemoryRevokedSessionMarks Marks { get; } = new();

    public FakeEntra Entra { get; } = new();

    public FakeKeycloak Keycloak { get; } = new();

    public CapturingProvider Logs { get; } = new();

    public AuthOptions Options { get; } = TestConfig.Options();

    public HttpClient Http { get; private set; } = null!;

    public IServiceProvider Services => _app!.Services;

    public static async Task<EndpointHost> StartAsync(Action<AuthOptions>? configure = null)
    {
        var host = new EndpointHost();
        configure?.Invoke(host.Options);
        await host.StartCoreAsync();
        return host;
    }

    private async Task StartCoreAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // as production holds it; see ProgramLoggingTests
        builder.Logging.AddProvider(Logs);

        builder.Services.AddSingleton(TestConfig.Wrap(Options));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISessionStore>(Sessions);
        builder.Services.AddSingleton<ILoginStateStore>(Logins);
        builder.Services.AddSingleton<IDistributedLock>(new FakeLock());
        builder.Services.AddSingleton<IEntraClient>(Entra);
        builder.Services.AddSingleton<IKeycloakExchanger>(Keycloak);
        builder.Services.AddSingleton<IRevokedSessionMarks>(Marks);
        builder.Services.AddSingleton(new RolesVersion(new SessionTokenProtector(SessionTokenProtector.GenerateKey())));
        builder.Services.AddSingleton(new PortalSessionId(new SessionTokenProtector(SessionTokenProtector.GenerateKey())));
        builder.Services.AddScoped<SessionValidator>();
        builder.Services.AddScoped<SignInService>();
        builder.Services.AddScoped<ForceRefreshHandler>();

        _app = builder.Build();
        _app.MapAuthEndpoints();
        _app.MapAdminEndpoints();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
    }

    /// <summary>Stores a live session and returns its cookie value.</summary>
    public string Seed(TimeSpan? jwtLifetime = null, bool force = false)
    {
        var id = SessionIds.NewRandom();
        var exp = DateTimeOffset.UtcNow + (jwtLifetime ?? TimeSpan.FromMinutes(30));
        var key = SessionIds.StorageKey(id);
        Sessions.Sessions[key] = new AuthSession(
            Jwt.Create(Subject, exp, TestConfig.ClientId, "requester"), exp, "rt-1", Subject, TestConfig.ClientId, force, DateTimeOffset.UtcNow.AddMinutes(-5));
        Sessions.Index.GetOrAdd(Subject, _ => []).Add(key);
        return id;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? sessionId = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (sessionId is not null)
        {
            request.Headers.Add("Cookie", $"{Options.Cookies.SessionName}={sessionId}");
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return Http.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
