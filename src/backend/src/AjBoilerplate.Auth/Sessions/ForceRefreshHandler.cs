using AjBoilerplate.Auth.Abstractions;

namespace AjBoilerplate.Auth.Sessions;

/// <summary>Flags every session of a subject so the next validate re-mints its JWT (picks up a role change).</summary>
public sealed partial class ForceRefreshHandler
{
    private readonly ISessionStore _sessions;
    private readonly ILogger<ForceRefreshHandler> _logger;

    public ForceRefreshHandler(ISessionStore sessions, ILogger<ForceRefreshHandler> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    /// <returns>How many sessions were flagged.</returns>
    public async Task<int> HandleAsync(string subject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var flagged = 0;
        foreach (var key in await _sessions.GetSessionKeysForSubjectAsync(subject, cancellationToken))
        {
            if (await _sessions.MarkForceRefreshAsync(key, cancellationToken))
            {
                flagged++;
            }
            else
            {
                await _sessions.RemoveFromSubjectIndexAsync(subject, key, cancellationToken);
            }
        }

        LogFlagged(_logger, flagged);
        return flagged;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Force refresh: {Count} session(s) flagged.")]
    private static partial void LogFlagged(ILogger logger, int count);
}
