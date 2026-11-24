using AjBoilerplate.Application.AuthenticationHistory;
using AjBoilerplate.Application.Persistence;
using AjBoilerplate.Domain.AuthenticationHistory;
using Microsoft.EntityFrameworkCore;

namespace AjBoilerplate.Infrastructure.Persistence;

public sealed class AuthenticationEventRepository : IAuthenticationEventRepository
{
    private readonly AppDbContext _context;

    public AuthenticationEventRepository(AppDbContext context) => _context = context;

    public async Task<bool> TryAddAsync(AuthenticationEvent authenticationEvent, CancellationToken cancellationToken)
    {
        _context.AuthenticationEvents.Add(authenticationEvent);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (SqlUniqueConstraintViolation.Matches(ex))
        {
            // Two first requests of one session raced and the unique index let one through.
            Forget(authenticationEvent);
            return false;
        }
        catch
        {
            Forget(authenticationEvent);
            throw;
        }
    }

    // The context is shared with the rest of the request. A row whose save failed would otherwise be
    // sent again by the request's own SaveChanges and fail that request instead.
    private void Forget(AuthenticationEvent authenticationEvent) =>
        _context.Entry(authenticationEvent).State = EntityState.Detached;
}
