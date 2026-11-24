using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AjBoilerplate.Auth.Tests;

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class InMemorySessionStore : ISessionStore
{
    public ConcurrentDictionary<string, AuthSession> Sessions { get; } = new();

    public ConcurrentDictionary<string, HashSet<string>> Index { get; } = new();

    public int Touches { get; private set; }

    public Action<string>? OnGet { get; set; }

    public Task<AuthSession?> GetAsync(string sessionKey, CancellationToken cancellationToken)
    {
        OnGet?.Invoke(sessionKey);
        return Task.FromResult(Sessions.TryGetValue(sessionKey, out var s) ? s : null);
    }

    public Task CreateAsync(string sessionKey, AuthSession session, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        Sessions[sessionKey] = session;
        Index.GetOrAdd(session.Subject, _ => []).Add(sessionKey);
        return Task.CompletedTask;
    }

    public Task TouchAsync(string sessionKey, string subject, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        Touches++;
        return Task.CompletedTask;
    }

    public Task StampSignInTimeAsync(string sessionKey, DateTimeOffset signedInAt, CancellationToken cancellationToken)
    {
        if (Sessions.TryGetValue(sessionKey, out var s) && s.CreatedAt <= DateTimeOffset.UnixEpoch)
        {
            Sessions[sessionKey] = s with { CreatedAt = signedInAt };
        }

        return Task.CompletedTask;
    }

    public Task UpdateTokensAsync(string sessionKey, string jwt, DateTimeOffset jwtExpiresAt, string? refreshToken, CancellationToken cancellationToken)
    {
        if (Sessions.TryGetValue(sessionKey, out var s))
        {
            Sessions[sessionKey] = s with
            {
                KeycloakJwt = jwt,
                JwtExpiresAt = jwtExpiresAt,
                EntraRefreshToken = refreshToken ?? s.EntraRefreshToken,
                ForceRefresh = false,
            };
        }

        return Task.CompletedTask;
    }

    public Task UpdateRefreshTokenAsync(string sessionKey, string refreshToken, CancellationToken cancellationToken)
    {
        if (Sessions.TryGetValue(sessionKey, out var s))
        {
            Sessions[sessionKey] = s with { EntraRefreshToken = refreshToken };
        }

        return Task.CompletedTask;
    }

    public Task<bool> MarkForceRefreshAsync(string sessionKey, CancellationToken cancellationToken)
    {
        if (Sessions.TryGetValue(sessionKey, out var s))
        {
            Sessions[sessionKey] = s with { ForceRefresh = true };
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>Every end the store was asked to make, in order: the reason and detail a portal would be told.</summary>
    public ConcurrentQueue<(string SessionKey, string Subject, SessionEnd Ending)> Ended { get; } = new();

    public Task DeleteAsync(string sessionKey, string subject, CancellationToken cancellationToken)
    {
        Sessions.TryRemove(sessionKey, out _);
        if (Index.TryGetValue(subject, out var set))
        {
            set.Remove(sessionKey);
        }

        return Task.CompletedTask;
    }

    public Task<bool> EndAsync(string sessionKey, string subject, SessionEnd ending, CancellationToken cancellationToken)
    {
        var removed = Sessions.TryRemove(sessionKey, out _);
        if (removed)
        {
            Ended.Enqueue((sessionKey, subject, ending));
        }

        if (Index.TryGetValue(subject, out var set))
        {
            set.Remove(sessionKey);
        }

        return Task.FromResult(removed);
    }

    public Task<IReadOnlyCollection<string>> GetSessionKeysForSubjectAsync(string subject, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>>(Index.TryGetValue(subject, out var set) ? [.. set] : []);

    public Task RemoveFromSubjectIndexAsync(string subject, string sessionKey, CancellationToken cancellationToken)
    {
        if (Index.TryGetValue(subject, out var set))
        {
            set.Remove(sessionKey);
        }

        return Task.CompletedTask;
    }
}

internal sealed class InMemoryRevokedSessionMarks : IRevokedSessionMarks
{
    public ConcurrentDictionary<string, TimeSpan> Marks { get; } = new();

    public Exception? Fault { get; set; }

    public Task MarkAsync(string sessionKey, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (Fault is not null)
        {
            throw Fault;
        }

        Marks[sessionKey] = lifetime;
        return Task.CompletedTask;
    }

    public Task<bool> IsMarkedAsync(string sessionKey, CancellationToken cancellationToken) =>
        Fault is not null ? throw Fault : Task.FromResult(Marks.ContainsKey(sessionKey));
}

internal sealed class InMemoryLoginStore : ILoginStateStore
{
    public ConcurrentDictionary<string, LoginState> States { get; } = new();

    public Task SaveAsync(string state, LoginState login, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        States[state] = login;
        return Task.CompletedTask;
    }

    public Task<LoginState?> TakeAsync(string state, CancellationToken cancellationToken) =>
        Task.FromResult(States.TryRemove(state, out var s) ? s : null);
}

internal sealed class InMemoryHandoffTicketStore(TimeProvider time) : IHandoffTicketStore
{
    public ConcurrentDictionary<string, (HandoffTicket Ticket, DateTimeOffset ExpiresAt)> Tickets { get; } = new();

    public Task SaveAsync(string ticketKey, HandoffTicket ticket, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        Tickets[ticketKey] = (ticket, time.GetUtcNow() + lifetime);
        return Task.CompletedTask;
    }

    public Task<HandoffTicket?> TakeAsync(string ticketKey, CancellationToken cancellationToken) =>
        Task.FromResult(Tickets.TryRemove(ticketKey, out var held) && held.ExpiresAt > time.GetUtcNow() ? held.Ticket : null);
}

internal sealed class FakeLock : IDistributedLock
{
    public bool HeldElsewhere { get; set; }

    public int Acquired { get; private set; }

    public int FailNext { get; set; }

    public List<string> Keys { get; } = [];

    public Task<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        Keys.Add(key);
        if (HeldElsewhere)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        if (FailNext > 0)
        {
            FailNext--;
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        Acquired++;
        return Task.FromResult<IAsyncDisposable?>(new Noop());
    }

    private sealed class Noop : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeEntra : IEntraClient
{
    public EntraTokenResult NextRefresh { get; set; } = new(EntraOutcome.Success, "entra-at-2", "entra-rt-2");

    public EntraTokenResult NextRedeem { get; set; } = new(EntraOutcome.Success, "entra-at-1", "entra-rt-1");

    public int Refreshes { get; private set; }

    public SignInPrompt? LastPrompt { get; private set; }

    public string? LastLoginHint { get; private set; }

    public Uri? LastRedirectUri { get; private set; }

    public Uri BuildAuthorizeUrl(Uri redirectUri, string state, string codeChallenge, SignInPrompt prompt, string? loginHint = null)
    {
        LastPrompt = prompt;
        LastLoginHint = loginHint;
        LastRedirectUri = redirectUri;
        return new($"https://login.example/authorize?state={state}&redirect_uri={Uri.EscapeDataString(redirectUri.AbsoluteUri)}");
    }

    public Task<EntraTokenResult> RedeemCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult(NextRedeem);

    public Action? OnRefresh { get; set; }

    public string? LastRefreshToken { get; private set; }

    public Task<EntraTokenResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        Refreshes++;
        LastRefreshToken = refreshToken;
        OnRefresh?.Invoke();
        return Task.FromResult(NextRefresh);
    }
}

internal sealed class FakeKeycloak : IKeycloakExchanger
{
    public ExchangeResult Next { get; set; } = ExchangeResult.Transient;

    public int Calls { get; private set; }

    public PortalOptions? LastPortal { get; private set; }

    public Task<ExchangeResult> ExchangeAsync(string entraAccessToken, PortalOptions portal, CancellationToken cancellationToken)
    {
        Calls++;
        LastPortal = portal;
        return Task.FromResult(Next);
    }
}

internal static class Jwt
{
    public static string Create(string sub, DateTimeOffset exp, string clientId = TestConfig.ClientId, params string[] roles)
    {
        var payload = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["exp"] = exp.ToUnixTimeSeconds(),
            ["resource_access"] = new Dictionary<string, object> { [clientId] = new { roles } },
        };

        return $"{Encode(new { alg = "RS256", typ = "JWT" })}.{Encode(payload)}.c2ln";
    }

    public static string CreateFlat(string sub, DateTimeOffset exp, string? azp, params string[] roles)
    {
        var payload = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["exp"] = exp.ToUnixTimeSeconds(),
        };

        if (azp is not null)
        {
            payload["azp"] = azp;
        }

        if (roles.Length > 0)
        {
            payload["roles"] = roles;
        }

        return $"{Encode(new { alg = "RS256", typ = "JWT" })}.{Encode(payload)}.c2ln";
    }

    public static string CreateRaw(string payloadJson) =>
        $"{Encode(new { alg = "RS256", typ = "JWT" })}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson))}.c2ln";

    private static string Encode(object value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
}

internal static class TestConfig
{
    public const string Host = "app.example.com";
    public const string Origin = "https://" + Host;
    public const string ClientId = "my-app";

    /// <summary>The second portal of <see cref="TwoPortals"/>.</summary>
    public const string OtherHost = "other.example.com";
    public const string OtherOrigin = "https://" + OtherHost;
    public const string OtherClientId = "other-app";
    public const string SessionCookie = "__Host-app_sid";

    public static AuthOptions Options() => new()
    {
        PublicOrigin = Origin,
        Entra = { Authority = "https://tenant.example.com/policy", ClientId = "entra-client", ClientSecret = "entra-secret" },
        Keycloak = { BaseUrl = "https://keycloak.example", SubjectIssuer = "entra" },
        Portal = { ClientId = ClientId, ClientSecret = "kc-secret" },
        Session =
        {
            EncryptionKey = Convert.ToBase64String(new byte[32]),
            RefreshLockLifetime = TimeSpan.FromMilliseconds(20),
            LockExpiryGrace = TimeSpan.FromMilliseconds(5),
            LockPollInterval = TimeSpan.FromMilliseconds(1),
        },
    };

    /// <summary>Portal #0 as in <see cref="Options"/>, plus one further portal on its own host: the shape a hand-off needs.</summary>
    public static AuthOptions TwoPortals()
    {
        var options = Options();
        options.Portals.Add(new PortalOptions { Host = OtherHost, ClientId = OtherClientId, ClientSecret = "kc-secret-2", DisplayName = "Other App" });
        return options;
    }

    public static IOptions<AuthOptions> Wrap(AuthOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    public static PortalRegistry Portals(AuthOptions options) => new(Wrap(options));
}

internal static class Log
{
    public static NullLogger<T> For<T>() => NullLogger<T>.Instance;
}

internal sealed class CapturingLogger : ILogger
{
    public List<CapturedEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var fields = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
            ? pairs.Where(p => p.Key != "{OriginalFormat}").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            : [];

        lock (Entries)
        {
            Entries.Add(new CapturedEntry(logLevel, eventId, formatter(state, exception), fields));
        }
    }
}

internal sealed record CapturedEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyDictionary<string, object?> Fields)
{
    public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value as string : null;
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly CapturingLogger _inner = new();

    public List<CapturedEntry> Entries => _inner.Entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}

internal sealed class CapturingProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Sink(Entries);

    public void Dispose()
    {
    }

    private sealed class Sink(ConcurrentQueue<CapturedEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                : [];
            entries.Enqueue(new CapturedEntry(logLevel, eventId, formatter(state, exception) + (exception is null ? string.Empty : " " + exception), fields));
        }
    }
}
