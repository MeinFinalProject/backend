using Microsoft.AspNetCore.Authorization;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Ta.Backend.Features.Attendance;
using Ta.Backend.Features.Devices;

namespace Ta.Backend.Common;

public static class ApiDocumentation
{
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "TA Backend API",
                    Version = "v1",
                    Description = "Device management, biometric gallery distribution, and attendance event ingestion."
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    [Credentials.DeviceScheme] = Bearer("Device credential issued through device registration."),
                    [Credentials.AdminScheme] = Bearer("Administration token configured for this backend.")
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var policy = context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<IAuthorizeData>().Select(a => a.Policy)
                    .FirstOrDefault(p => p is Credentials.DeviceScheme or Credentials.AdminScheme);
                if (policy is not null)
                {
                    operation.Security = [new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(policy, context.Document)] = []
                    }];
                    operation.Responses ??= new OpenApiResponses();
                    operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing, invalid, or disabled credential." });
                    operation.Responses.TryAdd("403", new OpenApiResponse { Description = "The credential cannot perform this operation." });
                    operation.Responses.TryAdd("429", new OpenApiResponse { Description = "Request limit reached. Retry after the Retry-After interval." });
                    operation.Responses.TryAdd("503", new OpenApiResponse { Description = "Database or gallery unavailable. Retry after the Retry-After interval." });
                }
                if (operation.OperationId == "GetGallery")
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "If-None-Match",
                        In = ParameterLocation.Header,
                        Description = "ETag from the previous gallery response.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                    foreach (var status in new[] { "200", "304" })
                    {
                        if (operation.Responses![status] is not OpenApiResponse response) continue;
                        response.Headers = new Dictionary<string, IOpenApiHeader>
                        {
                            ["ETag"] = new OpenApiHeader
                            {
                                Description = "Quoted checksum of the published gallery.",
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                            }
                        };
                    }
                }
                return Task.CompletedTask;
            });
            // Ingestion binds raw event objects so one malformed event does not reject
            // valid siblings. Document their schema without changing that behavior.
            options.AddSchemaTransformer(async (schema, context, ct) =>
            {
                if (context.JsonPropertyInfo?.Name == "schema_version")
                    schema.Default = JsonValue.Create(1);
                if (context.JsonTypeInfo.Type == typeof(AttendanceEnvelope))
                {
                    schema.Properties!["events"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Array,
                        MinItems = 1,
                        MaxItems = 256,
                        Items = await context.GetOrCreateSchemaAsync(typeof(AttendanceObservation), null, ct)
                    };
                }
            });
        });
        return services;
    }

    public static void MapApiDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "TA Backend API v1");
            options.DocumentTitle = "TA Backend API";
            options.ConfigObject.PersistAuthorization = false;
        });
    }

    private static OpenApiSecurityScheme Bearer(string description) => new()
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = description
    };
}

public sealed record ApiError(string Error);
