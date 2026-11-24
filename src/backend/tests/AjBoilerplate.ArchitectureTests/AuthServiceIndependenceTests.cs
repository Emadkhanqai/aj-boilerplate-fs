using System.Reflection;

namespace AjBoilerplate.ArchitectureTests;

/// <summary>
/// The Auth service is its own deployable: it shares no assembly with the layered API, in either direction.
/// Like <see cref="DependencyRuleTests"/> this reads COMPILED references, which a using-statement cannot hide.
/// </summary>
public sealed class AuthServiceIndependenceTests
{
    private static readonly Assembly Auth = typeof(AjBoilerplate.Auth.Program).Assembly;

    private static readonly string[] ApiSide =
    [
        "AjBoilerplate.Api", "AjBoilerplate.Application", "AjBoilerplate.Domain",
        "AjBoilerplate.Infrastructure", "AjBoilerplate.Contracts",
    ];

    private static readonly Assembly[] ApiSideAssemblies =
    [
        typeof(Program).Assembly,
        typeof(AjBoilerplate.Application.Items.IItemService).Assembly,
        typeof(AjBoilerplate.Domain.Items.Item).Assembly,
        typeof(AjBoilerplate.Infrastructure.Persistence.AppDbContext).Assembly,
        typeof(AjBoilerplate.Contracts.Common.ApiResponse).Assembly,
    ];

    private static HashSet<string> References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Auth_is_the_assembly_this_test_thinks_it_is() =>
        Assert.Equal("AjBoilerplate.Auth", Auth.GetName().Name);

    [Fact]
    public void Auth_references_none_of_the_api_side_assemblies()
    {
        var refs = References(Auth);
        foreach (var name in ApiSide)
        {
            Assert.False(refs.Contains(name), $"AjBoilerplate.Auth must not reference {name}.");
        }
    }

    [Fact]
    public void No_api_side_assembly_references_auth()
    {
        Assert.Equal(ApiSide.Length, ApiSideAssemblies.Length); // control: every api-side assembly is checked
        foreach (var assembly in ApiSideAssemblies)
        {
            Assert.False(References(assembly).Contains("AjBoilerplate.Auth"), $"{assembly.GetName().Name} must not reference AjBoilerplate.Auth.");
        }
    }
}
