using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Sessions;

namespace AjBoilerplate.Auth.SessionEvents;

public static class SessionEndedServiceCollectionExtensions
{
    /// <summary>
    /// The session-ended sender and its dispatch loop. The loop does nothing until a portal has a <c>SessionEvents:Url</c>, because
    /// nothing is queued for a portal without one; the SQL outbox (registered by <c>AddAuthSql</c>) is what it drains.
    /// </summary>
    public static IServiceCollection AddSessionEndedDelivery(this IServiceCollection services)
    {
        services.AddHttpClient(HttpSessionEndedSender.HttpClientName);
        services.AddHttpClient(MetadataGoogleIdTokenSource.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(3));
        services.AddSingleton<IGoogleIdTokenSource, MetadataGoogleIdTokenSource>();
        services.AddSingleton<ISessionEndedSender, HttpSessionEndedSender>();
        services.AddSingleton<SessionEndedDispatcher>();
        services.AddHostedService<SessionEndedDispatchService>();
        return services;
    }
}
