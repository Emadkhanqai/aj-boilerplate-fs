using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AjBoilerplate.Auth.Abstractions;
using AjBoilerplate.Auth.Configuration;

namespace AjBoilerplate.Auth.Sql;

internal sealed partial class ExpiredSessionSweeper : BackgroundService
{
    private const string LockKey = "session-sweep";
    private const int BatchSize = 500;

    private readonly SqlSessionStore _sessions;
    private readonly IDistributedLock _lock;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _absoluteLifetime;
    private readonly ILogger<ExpiredSessionSweeper> _logger;

    public ExpiredSessionSweeper(
        SqlSessionStore sessions,
        IDistributedLock distributedLock,
        IOptions<AuthOptions> options,
        ILogger<ExpiredSessionSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _sessions = sessions;
        _lock = distributedLock;
        _interval = options.Value.Session.SweepInterval;
        _absoluteLifetime = options.Value.Session.AbsoluteLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(_logger, ex);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var held = await _lock.TryAcquireAsync(LockKey, _interval, cancellationToken);
        if (held is null)
        {
            return; // another instance is sweeping
        }

        var total = 0;
        int deleted;
        do
        {
            deleted = await _sessions.EndExpiredAsync(BatchSize, _absoluteLifetime, cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize && !cancellationToken.IsCancellationRequested);

        if (total > 0)
        {
            LogSwept(_logger, total);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Swept {Count} expired session row(s); each ended with its session-ended event where the portal receives them.")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expired-session sweep failed; retrying on the next tick.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
