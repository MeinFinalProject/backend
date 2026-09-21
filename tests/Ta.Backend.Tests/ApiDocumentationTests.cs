using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ta.Backend.Tests;

public sealed class ApiDocumentationTests(BackendFixture fixture) : IClassFixture<BackendFixture>
{
    [Fact]
    public async Task Development_document_describes_versioned_routes_schemas_and_credentials()
    {
        await using var app = fixture.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("v1", root.GetProperty("info").GetProperty("version").GetString());
        var paths = root.GetProperty("paths");
        Assert.True(paths.EnumerateObject().Count() > 40);
        Assert.True(paths.TryGetProperty("/api/v1/auth/login", out _));
        Assert.True(paths.TryGetProperty("/api/v1/biometric-enrollments/{id}/samples", out _));
        Assert.All(paths.EnumerateObject(), p => Assert.StartsWith("/api/v1/", p.Name));
        var gallery = paths.GetProperty("/api/v1/gallery").GetProperty("get");
        Assert.True(gallery.GetProperty("security")[0].TryGetProperty("Device", out _));
        Assert.True(gallery.GetProperty("responses").TryGetProperty("304", out _));
        Assert.Contains(gallery.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "If-None-Match" && p.GetProperty("in").GetString() == "header");
        Assert.True(gallery.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        var register = paths.EnumerateObject().Single(p => p.Name.TrimEnd('/') == "/api/v1/admin/devices").Value.GetProperty("post");
        Assert.True(register.GetProperty("security")[0].TryGetProperty("Administrator", out _));
        Assert.True(register.GetProperty("responses").TryGetProperty("201", out _));
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("bearer", schemes.GetProperty("Device").GetProperty("scheme").GetString());
        Assert.Equal("bearer", schemes.GetProperty("Administrator").GetProperty("scheme").GetString());
        var enrollment = paths.EnumerateObject().Single(p => p.Name.TrimEnd('/') == "/api/v1/biometric-enrollments").Value.GetProperty("post");
        var studentSecurity = enrollment.GetProperty("security");
        Assert.Equal(1, studentSecurity.GetArrayLength());
        Assert.True(studentSecurity[0].TryGetProperty("Human", out _));
        var upload = paths.GetProperty("/api/v1/biometric-enrollments/{id}/samples/upload").GetProperty("post");
        Assert.Equal("UploadEnrollmentPhoto", upload.GetProperty("operationId").GetString());
        Assert.True(upload.GetProperty("security")[0].TryGetProperty("Human", out _));
        var photoForm = Resolve(root, upload.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema"));
        Assert.Equal(5, photoForm.GetProperty("properties").GetProperty("pose").GetProperty("enum").GetArrayLength());
        Assert.Equal("binary", photoForm.GetProperty("properties").GetProperty("image").GetProperty("format").GetString());
        var reviewSecurity = paths.GetProperty("/api/v1/biometric-enrollments/{id}/review").GetProperty("post").GetProperty("security");
        Assert.Contains(reviewSecurity.EnumerateArray(), r => r.TryGetProperty("Administrator", out _));
        Assert.Contains(reviewSecurity.EnumerateArray(), r => r.TryGetProperty("Human", out _));

        var ingest = paths.GetProperty("/api/v1/attendance-events/batch").GetProperty("post");
        var envelope = Resolve(root, ingest.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var events = envelope.GetProperty("properties").GetProperty("events");
        Assert.Equal(256, events.GetProperty("maxItems").GetInt32());
        var observation = Resolve(root, events.GetProperty("items"));
        Assert.Equal("string", observation.GetProperty("properties").GetProperty("track_id").GetProperty("type").GetString());
        Assert.True(observation.GetProperty("properties").TryGetProperty("pad_median_p_real", out _));
        var receipt = Resolve(root, ingest.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        Assert.True(receipt.GetProperty("properties").TryGetProperty("results", out _));

        var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui-bundle.js", await ui.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/swagger-ui-bundle.js")).StatusCode);
    }

    [Fact]
    public async Task Production_rate_limit_returns_retry_after_when_quota_is_exhausted()
    {
        await using var app = fixture.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        for (var i = 0; i < 300; i++)
        {
            using var response = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var limited = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Documentation_is_not_exposed_in_production()
    {
        await using var app = fixture.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Unversioned_and_unsupported_version_routes_are_not_mapped()
    {
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/gallery")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/gallery")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/gallery")).StatusCode);
    }

    private static JsonElement Resolve(JsonElement document, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference)) return schema;
        return document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
    }
}
