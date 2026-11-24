using AjBoilerplate.Auth.Configuration;
using AjBoilerplate.Auth.Identity;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.Tests;

public sealed class LoginStateSizeTests
{
    private readonly InMemoryLoginStore _logins = new();
    private readonly SignInService _sut;

    public LoginStateSizeTests()
    {
        var options = TestConfig.Options();
        _sut = new SignInService(new InMemorySessionStore(), _logins, new FakeEntra(), new FakeKeycloak(), TestConfig.Wrap(options), new ManualTime(DateTimeOffset.UnixEpoch.AddYears(56)), Log.For<SignInService>());
    }

    [Fact]
    public async Task An_oversized_return_path_is_not_stored()
    {
        var rd = "/" + new string('a', 1500);

        await _sut.BeginAsync(rd, SignInPrompt.None, default);

        Assert.Equal("/", Assert.Single(_logins.States.Values).ReturnPath);
    }

    [Fact]
    public async Task A_return_path_at_the_bound_is_kept()
    {
        var rd = "/budgets?" + new string('q', 1024 - "/budgets?".Length);

        await _sut.BeginAsync(rd, SignInPrompt.None, default);

        Assert.Equal(rd, Assert.Single(_logins.States.Values).ReturnPath);
        Assert.True(RedirectTarget.IsSafe(rd));
        Assert.False(RedirectTarget.IsSafe(rd + "q"));
    }
}
