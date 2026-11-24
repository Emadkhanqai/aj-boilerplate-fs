using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Security;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class SignInServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemorySessionStore _sessions = new();
    private readonly InMemoryLoginStore _logins = new();
    private readonly FakeEntra _entra = new();
    private readonly FakeKeycloak _keycloak = new();
    private readonly SignInService _sut;

    public SignInServiceTests()
    {
        var options = TestConfig.Options();
        _sut = new SignInService(_sessions, _logins, _entra, _keycloak, TestConfig.Wrap(options), new ManualTime(Now), Log.For<SignInService>());
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create("kc-user", Now.AddMinutes(5), TestConfig.ClientId, "requester"));
    }

    private async Task<(string State, string Binding)> StartAsync(string? rd = "/budgets/FY26-4412")
    {
        var start = await _sut.BeginAsync(rd, SignInPrompt.None, default);
        Assert.NotNull(start.AuthorizeUrl);
        var state = Assert.Single(_logins.States.Keys);
        return (state, start.BindingValue!);
    }

    [Fact]
    public async Task Begin_redirects_to_the_identity_provider_with_the_configured_callback_never_a_request_host()
    {
        var start = await _sut.BeginAsync("/", SignInPrompt.None, default);

        Assert.Contains(Uri.EscapeDataString("https://app.example.com/auth/callback"), start.AuthorizeUrl!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignInPrompt.None)]
    [InlineData(SignInPrompt.Login)]
    [InlineData(SignInPrompt.SelectAccount)]
    public async Task Begin_passes_the_prompt_to_the_identity_provider(SignInPrompt prompt)
    {
        Assert.Null(_entra.LastPrompt);   // control: the assertion below is about THIS call

        await _sut.BeginAsync("/", prompt, default);

        Assert.Equal(prompt, _entra.LastPrompt);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example")]
    public async Task Begin_never_stores_an_off_site_return_path(string rd)
    {
        var (state, _) = await StartAsync(rd);
        Assert.Equal("/", _logins.States[state].ReturnPath);
    }

    [Fact]
    public async Task Complete_creates_an_indexed_session_and_returns_the_original_path()
    {
        var (state, binding) = await StartAsync();

        var result = await _sut.CompleteAsync(state, "code", binding, default);

        Assert.Equal(LoginCompletionStatus.SignedIn, result.Status);
        Assert.Equal("/budgets/FY26-4412", result.ReturnPath);
        var session = _sessions.Sessions[SessionIds.StorageKey(result.SessionId!)];
        Assert.Equal("kc-user", session.Subject);
        Assert.Equal("entra-rt-1", session.EntraRefreshToken);
        Assert.Contains(SessionIds.StorageKey(result.SessionId!), _sessions.Index["kc-user"]);
    }

    [Fact]
    public async Task A_state_value_works_only_once()
    {
        var (state, binding) = await StartAsync();
        await _sut.CompleteAsync(state, "code", binding, default);

        var replay = await _sut.CompleteAsync(state, "code", binding, default);

        Assert.Equal(LoginCompletionStatus.Invalid, replay.Status);
    }

    [Fact]
    public async Task Callback_from_another_browser_is_rejected()
    {
        var (state, _) = await StartAsync();

        var result = await _sut.CompleteAsync(state, "code", SessionIds.NewRandom(), default);

        Assert.Equal(LoginCompletionStatus.Invalid, result.Status);
        Assert.Empty(_sessions.Sessions);
    }

    [Fact]
    public async Task No_role_for_the_portal_is_403_not_a_redirect()
    {
        _keycloak.Next = new ExchangeResult(ExchangeOutcome.Success, Jwt.Create("kc-user", Now.AddMinutes(5), "cad", "focal_point"));
        var (state, binding) = await StartAsync();

        var result = await _sut.CompleteAsync(state, "code", binding, default);

        Assert.Equal(LoginCompletionStatus.NoAccess, result.Status);
        Assert.Empty(_sessions.Sessions);
    }

    [Fact]
    public async Task Keycloak_denial_is_no_access()
    {
        _keycloak.Next = ExchangeResult.Denied;
        var (state, binding) = await StartAsync();

        var result = await _sut.CompleteAsync(state, "code", binding, default);

        Assert.Equal(LoginCompletionStatus.NoAccess, result.Status);
    }

    [Fact]
    public async Task Keycloak_transient_is_unavailable_not_a_redirect()
    {
        _keycloak.Next = ExchangeResult.Transient;
        var (state, binding) = await StartAsync();

        var result = await _sut.CompleteAsync(state, "code", binding, default);

        Assert.Equal(LoginCompletionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Sign_out_removes_only_this_session()
    {
        var (s1, b1) = await StartAsync();
        var first = await _sut.CompleteAsync(s1, "code", b1, default);
        var (s2, b2) = await StartAsync();
        var second = await _sut.CompleteAsync(s2, "code", b2, default);

        await _sut.SignOutAsync(first.SessionId, default);

        Assert.False(_sessions.Sessions.ContainsKey(SessionIds.StorageKey(first.SessionId!)));
        Assert.True(_sessions.Sessions.ContainsKey(SessionIds.StorageKey(second.SessionId!)));
    }
}
