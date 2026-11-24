using Microsoft.AspNetCore.Mvc.Testing;

namespace AjBoilerplate.IntegrationTests.Support;

/// <summary>
/// <c>ApiFactory.CreateClientAs</c> for a factory derived with <c>WithWebHostBuilder</c> (extra configuration
/// for one test class), which is a different type and so has no such method.
/// </summary>
public static class FactoryExtensions
{
    public static HttpClient CreateClientAsRole(this WebApplicationFactory<Program> factory, string role, string? userId = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        }

        return client;
    }
}
