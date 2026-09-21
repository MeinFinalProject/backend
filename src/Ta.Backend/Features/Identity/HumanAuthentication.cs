using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ta.Backend.Features.Devices;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Identity;

public sealed class HumanAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, BackendDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) && Request.Path == "/api/v1/live"
            && Request.Query["access_token"] is { Count: 1 } queryToken)
            header = "Bearer " + queryToken.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..];
        if (token.Length != 64) return AuthenticateResult.Fail("Invalid credential.");
        var hash = Credentials.Hash(token);
        var account = await (from session in db.Set<AccountSession>()
            join user in db.Set<Account>() on session.AccountId equals user.AccountId
            where session.AccountSessionTokenHash == hash && !session.AccountSessionRevoked
                && session.AccountSessionExpiresAt > DateTimeOffset.UtcNow && user.AccountStatus == "approved"
            select new { user.AccountId, user.AccountRole, session.AccountSessionId, session.AccountSessionExpiresAt }).SingleOrDefaultAsync(Context.RequestAborted);
        if (account is null) return AuthenticateResult.Fail("Invalid credential.");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, account.AccountId.ToString()),
            new Claim(ClaimTypes.Role, account.AccountRole),
            new Claim("session_id", account.AccountSessionId.ToString())], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = account.AccountSessionExpiresAt }, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
