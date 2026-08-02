using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AjBoilerplate.ArchitectureTests;

/// <summary>
/// Conventions every controller must follow. These catch the two mistakes that are cheap to make
/// and expensive to find: an endpoint that quietly ships unauthenticated, and a route that skips the
/// version prefix and can therefore never be evolved without breaking clients.
/// </summary>
public sealed class ControllerConventionTests
{
    private static readonly IReadOnlyList<Type> Controllers = typeof(Program).Assembly
        .GetTypes()
        .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
        .ToList();

    [Fact]
    public void There_is_at_least_one_controller_to_check() =>
        // Guards the two tests below: a bad filter that matched nothing would make them pass vacuously.
        Assert.NotEmpty(Controllers);

    [Fact]
    public void Every_controller_is_authorized_or_explicitly_anonymous()
    {
        foreach (var controller in Controllers)
        {
            var hasAuthorize = controller.GetCustomAttribute<AuthorizeAttribute>(inherit: true) is not null;
            var hasAllowAnonymous = controller.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;

            Assert.True(
                hasAuthorize || hasAllowAnonymous,
                $"{controller.Name} must carry [Authorize] or an explicit [AllowAnonymous]. " +
                "An endpoint's authentication posture is never allowed to be implicit.");
        }
    }

    [Fact]
    public void Every_controller_route_is_versioned()
    {
        foreach (var controller in Controllers)
        {
            var route = controller.GetCustomAttribute<RouteAttribute>(inherit: true)?.Template;

            Assert.True(
                route is not null && route.StartsWith("api/v", StringComparison.OrdinalIgnoreCase),
                $"{controller.Name} must declare a versioned route (api/v1/...), but had '{route ?? "(none)"}'.");
        }
    }
}
