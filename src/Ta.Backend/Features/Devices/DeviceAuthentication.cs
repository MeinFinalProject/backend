using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Devices;

public static class Credentials
{
    public const string DeviceScheme = "Device";
    public const string AdminScheme = "Administrator";
    public static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class BearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, BackendDbContext database, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = authorization[7..];
        if (token.Length is < 32 or > 4096 || token.Any(c => c <= 32 || c >= 127))
            return AuthenticateResult.Fail("Invalid credential.");

        string subject;
        if (Scheme.Name == Credentials.AdminScheme)
        {
            var expected = configuration["Administration:Token"];
            if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(token)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
                return AuthenticateResult.Fail("Invalid credential.");
            subject = "administrator";
        }
        else
        {
            var hash = Credentials.Hash(token);
            var device = await database.Devices.AsNoTracking().SingleOrDefaultAsync(
                d => d.DeviceTokenHash == hash && d.DeviceEnabled, Context.RequestAborted);
            if (device is null) return AuthenticateResult.Fail("Invalid credential.");
            subject = device.DeviceId;
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
