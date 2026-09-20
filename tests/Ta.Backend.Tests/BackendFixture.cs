using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Ta.Backend.Features.Devices;
using Ta.Backend.Persistence;

namespace Ta.Backend.Tests;

public sealed class BackendFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string AdminToken { get; } = Credentials.Generate();
    public string ModelHash { get; } = new('a', 64);
    private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
    private string baseConnection = "";
    public string ConnectionString { get; private set; } = "";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Backend", ConnectionString);
        builder.UseSetting("Administration:Token", AdminToken);
        builder.UseSetting("Biometrics:ModelSha256", ModelHash);
    }

    public BackendDbContext OpenDatabase() => new(new DbContextOptionsBuilder<BackendDbContext>()
        .UseNpgsql(ConnectionString, pg => pg.MigrationsHistoryTable("ef_migration_history")).Options);

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>().AddEnvironmentVariables().Build();
        baseConnection = config["Tests:Postgres"]
            ?? throw new InvalidOperationException("Set Tests:Postgres in user secrets, or Tests__Postgres in the environment. Tests require real PostgreSQL.");
        var parsed = new NpgsqlConnectionStringBuilder(baseConnection);
        if (parsed.Database != "ta_backend_test") throw new InvalidOperationException("Tests must use the dedicated ta_backend_test database.");
        await using var connection = new NpgsqlConnection(baseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
        await command.ExecuteNonQueryAsync();
        parsed.SearchPath = schema;
        ConnectionString = parsed.ConnectionString;
        await using var db = OpenDatabase();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        if (string.IsNullOrEmpty(baseConnection)) return;
        await using var connection = new NpgsqlConnection(baseConnection);
        await connection.OpenAsync();
        // schema is generated above, never user input; no shared tables are touched.
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    public HttpClient Client(string? token = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}
