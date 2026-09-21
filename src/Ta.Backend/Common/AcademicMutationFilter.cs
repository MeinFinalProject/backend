using Microsoft.EntityFrameworkCore;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Identity;
using System.Security.Claims;
using Ta.Backend.Persistence;

namespace Ta.Backend.Common;

// One short transaction boundary for lab-scale academic mutations. Ingestion uses
// the same lock so temporal membership/room changes cannot race event evaluation.
public sealed class AcademicMutationFilter(BackendDbContext db) : IEndpointFilter
{
    public static Task Lock(BackendDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(741829302)", ct);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method)) return await next(context);
        var ct = context.HttpContext.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Lock(db, ct);
        if (context.HttpContext.User.FindFirstValue("session_id") is {} sessionClaim)
        {
            var sessionId = Guid.Parse(sessionClaim);
            DomainException.Require(await (from s in db.Set<AccountSession>() join a in db.Set<Account>() on s.AccountId equals a.AccountId
                where s.AccountSessionId == sessionId && !s.AccountSessionRevoked && s.AccountSessionExpiresAt > DateTimeOffset.UtcNow
                    && a.AccountStatus == "approved" select s).AnyAsync(ct), "account_session_revoked", 401);
        }
        var result = await next(context);
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        // Failed logins deliberately commit their lockout counters; domain exceptions roll back.
        if (status < 400 && context.HttpContext.User.Identity?.IsAuthenticated == true)
            AuditLog.Add(db, context.HttpContext.User, context.HttpContext.Request.Method,
                context.HttpContext.Request.Path.ToString());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (status < 400)
        {
            var services = context.HttpContext.RequestServices;
            var updates = services.GetRequiredService<Ta.Backend.Features.Realtime.RealtimeUpdates>();
            updates.AcademicCommitted();
            if (services.GetRequiredService<Ta.Backend.Features.Biometrics.GalleryPublisher>().Published)
                updates.GalleryCommitted();
        }
        return result;
    }
}
