using System.Reflection;
using AjBoilerplate.Application.Items;
using AjBoilerplate.Contracts.Common;
using AjBoilerplate.Domain.Items;
using AjBoilerplate.Infrastructure.Persistence;

namespace AjBoilerplate.ArchitectureTests;

/// <summary>
/// Enforces the Clean Architecture dependency rule by inspecting each layer assembly's COMPILED
/// references. Dependencies must point inward only.
///
/// Compiled references, not source conventions: a using-statement grep can be satisfied by a fully
/// qualified name, but an assembly reference cannot be hidden. If one of these fails, the fix is
/// almost never to relax the test — it is to move the type that leaked across the boundary.
/// </summary>
public sealed class DependencyRuleTests
{
    private const string Prefix = "AjBoilerplate.";

    private static readonly Assembly Domain = typeof(Item).Assembly;
    private static readonly Assembly Application = typeof(IItemService).Assembly;
    private static readonly Assembly Infrastructure = typeof(AppDbContext).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;
    private static readonly Assembly Contracts = typeof(ApiResponse).Assembly;

    [Fact]
    public void Domain_depends_on_nothing_internal() =>
        AssertDoesNotReference(Domain,
            "AjBoilerplate.Application", "AjBoilerplate.Infrastructure", "AjBoilerplate.Api", "AjBoilerplate.Contracts");

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api() =>
        AssertDoesNotReference(Application, "AjBoilerplate.Infrastructure", "AjBoilerplate.Api");

    [Fact]
    public void Application_does_not_depend_on_the_wire_contracts() =>
        // The Application layer owns its own models; the Api maps them to the wire DTOs. Otherwise a
        // breaking API change would force a change to the use cases themselves.
        AssertDoesNotReference(Application, "AjBoilerplate.Contracts");

    [Fact]
    public void Infrastructure_does_not_depend_on_api() =>
        AssertDoesNotReference(Infrastructure, "AjBoilerplate.Api");

    [Fact]
    public void Contracts_contains_no_dependency_on_other_layers() =>
        AssertDoesNotReference(Contracts,
            "AjBoilerplate.Domain", "AjBoilerplate.Application", "AjBoilerplate.Infrastructure", "AjBoilerplate.Api");

    [Fact]
    public void Api_does_not_reference_domain_directly() =>
        // The Api works through Application and Contracts; it never binds to a Domain type. This is
        // what keeps entities out of request/response bodies.
        AssertDoesNotReference(Api, "AjBoilerplate.Domain");

    private static IEnumerable<string> ReferencedInternalAssemblies(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal));

    private static void AssertDoesNotReference(Assembly assembly, params string[] forbidden)
    {
        var refs = ReferencedInternalAssemblies(assembly).ToHashSet(StringComparer.Ordinal);
        foreach (var name in forbidden)
        {
            Assert.False(refs.Contains(name), $"{assembly.GetName().Name} must not reference {name}.");
        }
    }
}
