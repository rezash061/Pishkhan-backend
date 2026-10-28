using System.Diagnostics;
using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Telemetry;
using Pishkhan.SharedKernel;

namespace Pishkhan.Infrastructure.Application;

/// <summary>A use case that changes state. Plain interfaces instead of a mediator library (MediatR is commercially licensed).</summary>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>A use case that reads state.</summary>
public interface IQueryHandler<in TQuery, TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Marker for "no value".</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}

/// <summary>Runs the FluentValidation validators of the request before the handler. Failures become message-key errors.</summary>
public sealed class ValidatingCommandHandler<TCommand, TResult>(ICommandHandler<TCommand, TResult> inner, IEnumerable<IValidator<TCommand>> validators)
    : ICommandHandler<TCommand, TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var error = await Validation.ValidateAsync(command, validators, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        using var activity = PishkhanTelemetry.Application.StartActivity(typeof(TCommand).Name);
        var result = await inner.HandleAsync(command, cancellationToken);
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Error!.Code);
        }

        return result;
    }
}

public sealed class ValidatingQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> inner, IEnumerable<IValidator<TQuery>> validators)
    : IQueryHandler<TQuery, TResult>
{
    public async Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken)
    {
        var error = await Validation.ValidateAsync(query, validators, cancellationToken);
        return error ?? await inner.HandleAsync(query, cancellationToken);
    }
}

public static class Validation
{
    public static async Task<Error?> ValidateAsync<T>(T request, IEnumerable<IValidator<T>> validators, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var fields = failures
            .GroupBy(f => ToCamelCase(f.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorCode).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return Error.Validation("validation.failed", fields);
    }

    private static string ToCamelCase(string name) =>
        string.Join('.', name.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}

public static class ApplicationExtensions
{
    /// <summary>Registers handlers and validators of a module assembly, decorated with validation.</summary>
    public static IServiceCollection AddHandlersFrom(this IServiceCollection services, Assembly assembly)
    {
        services.Scan(s => s.FromAssemblies(assembly)
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime());
        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Singleton, includeInternalTypes: true);
        return services;
    }

    /// <summary>Call once, after all modules registered their handlers.</summary>
    public static IServiceCollection DecorateHandlers(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)))
        {
            services.Decorate(typeof(ICommandHandler<,>), typeof(ValidatingCommandHandler<,>));
        }

        if (services.Any(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)))
        {
            services.Decorate(typeof(IQueryHandler<,>), typeof(ValidatingQueryHandler<,>));
        }

        return services;
    }
}
