using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ta.Backend.Common;
using Ta.Backend.Features.Attendance;
using Ta.Backend.Features.Biometrics;
using Ta.Backend.Features.Devices;
using Ta.Backend.Persistence;
using Ta.Backend.Features.Identity;
using Ta.Backend.Features.AcademicManagement;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Backend")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Backend with user secrets or environment variables.");
if (builder.Configuration["Administration:Token"] is not { Length: >= 64 } adminToken
    || adminToken.Any(c => c <= 32 || c >= 127))
    throw new InvalidOperationException("Configure a random Administration:Token of at least 64 printable characters.");
var modelHash = builder.Configuration["Biometrics:ModelSha256"];
if (modelHash is not { Length: 64 } || modelHash.Any(c => !char.IsAsciiHexDigitLower(c)))
    throw new InvalidOperationException("Configure Biometrics:ModelSha256 with the lowercase SHA-256 of the Edge ArcFace model.");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 36 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = WireJson.Options.PropertyNamingPolicy;
    o.SerializerOptions.PropertyNameCaseInsensitive = false;
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
});
builder.Services.AddDbContext<BackendDbContext>(o => o.UseNpgsql(connectionString,
    pg => pg.MigrationsHistoryTable("ef_migration_history")));
builder.Services.AddApiDocumentation();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AcademicAccess>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<AttendanceEvaluator>();
builder.Services.AddScoped<GalleryPublisher>();
builder.Services.AddSingleton<IEmbeddingExtractor, NativeEmbeddingExtractor>();
builder.Services.AddAuthentication(Credentials.DeviceScheme)
    .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(Credentials.DeviceScheme, _ => { })
    .AddScheme<AuthenticationSchemeOptions, BearerAuthenticationHandler>(Credentials.AdminScheme, _ => { })
    .AddScheme<AuthenticationSchemeOptions, HumanAuthentication>(Roles.HumanScheme, _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Credentials.DeviceScheme, p => p.AddAuthenticationSchemes(Credentials.DeviceScheme).RequireAuthenticatedUser())
    .AddPolicy(Credentials.AdminScheme, p => p.AddAuthenticationSchemes(Credentials.AdminScheme, Roles.HumanScheme).RequireRole(Roles.Administrator))
    .AddPolicy(Roles.AdministratorPolicy, p => p.AddAuthenticationSchemes(Credentials.AdminScheme, Roles.HumanScheme).RequireRole(Roles.Administrator))
    .AddPolicy(Roles.HumanPolicy, p => p.AddAuthenticationSchemes(Roles.HumanScheme).RequireAuthenticatedUser())
    .AddPolicy("Academic", p => p.AddAuthenticationSchemes(Credentials.AdminScheme, Roles.HumanScheme).RequireAuthenticatedUser())
    .AddPolicy(Roles.StaffPolicy, p => p.AddAuthenticationSchemes(Credentials.AdminScheme, Roles.HumanScheme).RequireRole(Roles.Administrator, Roles.Lecturer))
    .AddPolicy(Roles.StudentPolicy, p => p.AddAuthenticationSchemes(Roles.HumanScheme).RequireRole(Roles.Student));
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

var app = builder.Build();
// Edge disables redirects. Refuse insecure requests instead of redirecting credentials.
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps) { context.Response.StatusCode = 400; return; }
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path == ApiRoutes.AttendanceBatch)
    {
        var size = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 1024 * 1024;
    }
    try { await next(context); }
    catch (DomainException e)
    {
        context.Response.Clear();
        context.Response.StatusCode = e.Status;
        await context.Response.WriteAsJsonAsync(new { error = e.Message });
    }
    catch (Exception e) when (e is NpgsqlException or DbUpdateException)
    {
        app.Logger.LogError("Database request failed ({ErrorType}); trace {TraceId}", e.GetType().Name, context.TraceIdentifier);
        context.Response.Clear();
        context.Response.StatusCode = 503;
        context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsJsonAsync(new { error = "database_unavailable" });
    }
    catch (BadHttpRequestException e)
    {
        context.Response.StatusCode = e.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = "invalid_request" });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception e)
    {
        app.Logger.LogError("Request failed ({ErrorType}); trace {TraceId}", e.GetType().Name, context.TraceIdentifier);
        context.Response.Clear();
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "internal_error" });
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).ExcludeFromDescription();
app.MapGet("/health/ready", async (BackendDbContext db, CancellationToken ct) =>
{
    _ = await db.Devices.AsNoTracking().AnyAsync(ct);
    return Results.Ok(new { status = "ready" });
}).ExcludeFromDescription();
var v1 = app.MapGroup(ApiRoutes.V1).WithGroupName("v1");
v1.MapDeviceEndpoints();
v1.MapGalleryEndpoints();
v1.MapAttendanceEndpoints();
v1.MapIdentityEndpoints();
v1.MapAcademicEndpoints();
v1.MapRegistrationEndpoints();
v1.MapSessionEndpoints();
v1.MapAcademicAttendanceEndpoints();
v1.MapDeviceAcademicEndpoints();
v1.MapEnrollmentEndpoints();
app.MapApiDocumentation();
app.Run();

public partial class Program;
