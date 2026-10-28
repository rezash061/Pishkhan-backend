using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Pishkhan.Web.OpenApi;

public static class ApiDocuments
{
    /// <summary>Panel API (<c>/api/v1</c>), used by the user panel.</summary>
    public const string Api = "api";

    /// <summary>Public API (<c>/api/v1/public</c>), used by the website.</summary>
    public const string Public = "public";

    /// <summary>Back-office API (<c>/admin/v1</c>), used by the admin panel.</summary>
    public const string Admin = "admin";
}

/// <summary>Request and response examples (with Persian data) attached to an endpoint.</summary>
public sealed record OpenApiExampleMetadata(object? Request, object? Response);

public static class OpenApiSetup
{
    private static readonly System.Text.Json.JsonSerializerOptions ExampleJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IServiceCollection AddPishkhanOpenApi(this IServiceCollection services, params string[] documents)
    {
        foreach (var name in documents)
        {
            services.AddOpenApi(name, o =>
            {
                o.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
                o.ShouldInclude = d => string.Equals(d.GroupName, name, StringComparison.Ordinal);
                o.AddDocumentTransformer((document, _, _) =>
                {
                    document.Info = new OpenApiInfo
                    {
                        Title = name switch
                        {
                            ApiDocuments.Admin => "Pishkhan Admin API",
                            ApiDocuments.Public => "Pishkhan Public API",
                            _ => "Pishkhan Panel API",
                        },
                        Version = "v1",
                        Description = "Contract owned by the backend (overview section 6). Errors are RFC 9457 Problem Details whose `errors` hold message keys.",
                    };
                    document.Servers = [];
                    if (name != ApiDocuments.Public)
                    {
                        document.Components ??= new OpenApiComponents();
                        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                        document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                        {
                            Type = SecuritySchemeType.Http,
                            Scheme = "bearer",
                            BearerFormat = "JWT",
                            Description = name == ApiDocuments.Admin ? "Staff access token" : "User access token (sent by the BFF)",
                        };
                    }

                    return Task.CompletedTask;
                });
                o.AddOperationTransformer((operation, context, _) =>
                {
                    var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                    if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
                    {
                        operation.Security ??= [];
                        operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", context.Document)] = [] });
                    }

                    if (metadata.OfType<Http.IdempotentAttribute>().Any())
                    {
                        operation.Parameters ??= [];
                        operation.Parameters.Add(new OpenApiParameter
                        {
                            Name = Http.IdempotencyExtensions.HeaderName,
                            In = ParameterLocation.Header,
                            Required = false,
                            Description = "UUID; retries with the same key return the first response.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                        });
                    }

                    if (metadata.OfType<OpenApiExampleMetadata>().LastOrDefault() is { } example)
                    {
                        if (example.Request is not null && operation.RequestBody?.Content is { } requestContent)
                        {
                            foreach (var media in requestContent.Values.OfType<OpenApiMediaType>())
                            {
                                media.Example = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(example.Request, ExampleJson));
                            }
                        }

                        if (example.Response is not null && operation.Responses is { } responses)
                        {
                            foreach (var (code, response) in responses)
                            {
                                if (code.StartsWith('2') && response.Content is { } content)
                                {
                                    foreach (var media in content.Values.OfType<OpenApiMediaType>())
                                    {
                                        media.Example = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(example.Response, ExampleJson));
                                    }
                                }
                            }
                        }
                    }

                    return Task.CompletedTask;
                });
            });
        }

        return services;
    }

    public static TBuilder WithExamples<TBuilder>(this TBuilder builder, object? request, object? response = null)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new OpenApiExampleMetadata(request, response));
        return builder;
    }
}

public static class EndpointGroups
{
    /// <summary>Panel endpoints: <c>/api/v1</c>, user token, per-user rate limit.</summary>
    public static RouteGroupBuilder MapPanelApi(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api/v1").WithGroupName(ApiDocuments.Api).RequireAuthorization(Auth.AuthPolicies.User).RequireRateLimiting(Http.RateLimitPolicies.User);

    /// <summary>Panel endpoints that need no session (sign in, refresh).</summary>
    public static RouteGroupBuilder MapPanelAnonymousApi(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api/v1").WithGroupName(ApiDocuments.Api).AllowAnonymous();

    /// <summary>Website endpoints: <c>/api/v1/public</c>, anonymous, per-IP rate limit.</summary>
    public static RouteGroupBuilder MapPublicApi(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api/v1/public").WithGroupName(ApiDocuments.Public).AllowAnonymous().RequireRateLimiting(Http.RateLimitPolicies.Public);

    /// <summary>Back-office endpoints: <c>/admin/v1</c>, staff token.</summary>
    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder app) =>
        app.MapGroup("/admin/v1").WithGroupName(ApiDocuments.Admin).RequireAuthorization(Auth.AuthPolicies.Staff);

    public static RouteGroupBuilder MapAdminAnonymousApi(this IEndpointRouteBuilder app) =>
        app.MapGroup("/admin/v1").WithGroupName(ApiDocuments.Admin).AllowAnonymous().RequireRateLimiting(Http.RateLimitPolicies.Auth);

    /// <summary>Problem responses every endpoint may return.</summary>
    public static RouteHandlerBuilder WithStandardProblems(this RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status429TooManyRequests);
}
