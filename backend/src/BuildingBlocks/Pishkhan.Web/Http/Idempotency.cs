using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Web.Auth;
using StackExchange.Redis;

namespace Pishkhan.Web.Http;

/// <summary>Marks an endpoint as accepting the <c>Idempotency-Key</c> header.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IdempotentAttribute : Attribute;

public static class IdempotencyExtensions
{
    public const string HeaderName = "Idempotency-Key";

    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new IdempotentAttribute());
        return builder;
    }

    /// <summary>
    /// Stores <c>(key, user, route, request hash) → response</c> in the Redis state instance for 24 hours (backend plan 3.3).
    /// The same key with the same request replays the stored response; with a different body returns 422; while the first
    /// request is still running, returns 409.
    /// </summary>
    public static IApplicationBuilder UsePishkhanIdempotency(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IdempotentAttribute>() is null
            || !HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsPut(context.Request.Method) && !HttpMethods.IsPatch(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method)
            || !context.Request.Headers.TryGetValue(HeaderName, out var keyValues))
        {
            await next();
            return;
        }

        if (!Guid.TryParse(keyValues.ToString(), out var idempotencyKey))
        {
            await Results.Problem(statusCode: 400, type: ProblemTypes.Validation, title: "validation.idempotency_key.invalid").ExecuteAsync(context);
            return;
        }

        var services = context.RequestServices;
        var redis = services.GetRequiredKeyedService<IConnectionMultiplexer>(RedisInstances.State).GetDatabase();
        var keys = services.GetRequiredService<RedisKeys>();
        var user = services.GetRequiredService<ICurrentUser>();
        var principal = user.IsAuthenticated ? user.UserId.ToString("N") : "anon:" + context.Connection.RemoteIpAddress;
        var redisKey = keys.For("idem", $"{principal}:{idempotencyKey:N}");

        context.Request.EnableBuffering();
        var hash = await HashRequestAsync(context.Request);
        context.Request.Body.Position = 0;

        var stored = await redis.StringGetAsync(redisKey);
        if (stored.HasValue)
        {
            var record = JsonSerializer.Deserialize<StoredResponse>(stored.ToString())!;
            if (!string.Equals(record.RequestHash, hash, StringComparison.Ordinal))
            {
                await Results.Problem(statusCode: 422, type: ProblemTypes.IdempotencyMismatch, title: "idempotency.key_reused").ExecuteAsync(context);
                return;
            }

            if (record.InProgress)
            {
                await Results.Problem(statusCode: 409, type: ProblemTypes.Conflict, title: "idempotency.in_progress").ExecuteAsync(context);
                return;
            }

            context.Response.StatusCode = record.Status;
            context.Response.ContentType = record.ContentType;
            context.Response.Headers["Idempotent-Replayed"] = "true";
            await context.Response.Body.WriteAsync(Convert.FromBase64String(record.Body));
            return;
        }

        var placeholder = JsonSerializer.Serialize(new StoredResponse(hash, true, 0, null, string.Empty));
        if (!await redis.StringSetAsync(redisKey, placeholder, TimeSpan.FromMinutes(5), When.NotExists))
        {
            await Results.Problem(statusCode: 409, type: ProblemTypes.Conflict, title: "idempotency.in_progress").ExecuteAsync(context);
            return;
        }

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next();
        }
        catch
        {
            await redis.KeyDeleteAsync(redisKey);
            throw;
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        if (context.Response.StatusCode < 500)
        {
            var record = new StoredResponse(hash, false, context.Response.StatusCode, context.Response.ContentType, Convert.ToBase64String(buffer.ToArray()));
            await redis.StringSetAsync(redisKey, JsonSerializer.Serialize(record), TimeSpan.FromHours(24));
        }
        else
        {
            await redis.KeyDeleteAsync(redisKey);
        }

        await buffer.CopyToAsync(original);
    });

    private static async Task<string> HashRequestAsync(HttpRequest request)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes(request.Method + " " + request.Path + request.QueryString + "\n"));
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            sha.AppendData(chunk, 0, read);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private sealed record StoredResponse(string RequestHash, bool InProgress, int Status, string? ContentType, string Body);
}
