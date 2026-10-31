using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.AI;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Agent.Persistence;

namespace Pishkhan.Modules.Agent.Services;

public sealed class AiSettingsOptions
{
    public const string Section = "Ai:Settings";

    /// <summary>How often every host reloads the back-office AI settings; the host that saves a change reloads at once.</summary>
    public int RefreshSeconds { get; set; } = 15;
}

/// <summary>
/// Configuration merged with the back office (admin plan 4.5): a provider set there replaces the configured one of that name (a
/// disabled one is removed); a task with routes there uses only those; prompt versions written there are added to the configured
/// ones, and an activation there picks the active version. Held as a snapshot, so the model router never waits on the database.
/// </summary>
public sealed class AiSettingsStore(
    IOptionsMonitor<AiOptions> configured, IServiceScopeFactory scopes, ISecretProtector secrets, ILogger<AiSettingsStore> logger) : IAiSettings
{
    public const string ApiKeyPurpose = "agent.ai_provider.api_key";

    private readonly Lock _gate = new();
    private Snapshot? _snapshot;

    /// <summary>Provider, route and prompt rows as last loaded, before merging (the back office shows where each value comes from).</summary>
    public sealed record Snapshot(AiOptions Effective, IReadOnlySet<string> DbProviders, IReadOnlySet<string> DbTasks, IReadOnlyDictionary<string, string> Activations);

    public AiOptions Current => GetSnapshot().Effective;

    public Snapshot GetSnapshot()
    {
        if (_snapshot is { } loaded)
        {
            return loaded;
        }

        // First use before the refresher ran (a tool, or a request during startup): load once, synchronously.
        lock (_gate)
        {
            return _snapshot ??= Task.Run(() => LoadAsync(CancellationToken.None)).GetAwaiter().GetResult();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var snapshot = await LoadAsync(cancellationToken);
        Volatile.Write(ref _snapshot, snapshot);
    }

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        var baseline = configured.CurrentValue;
        var effective = Clone(baseline);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
            var providers = await db.AiProviders.AsNoTracking().ToListAsync(ct);
            var routes = await db.AiRoutes.AsNoTracking().OrderBy(r => r.Task).ThenBy(r => r.Position).ToListAsync(ct);
            var versions = await db.PromptVersions.AsNoTracking().ToListAsync(ct);
            var activations = await db.PromptActivations.AsNoTracking().ToDictionaryAsync(a => a.Key, a => a.Version, StringComparer.Ordinal, ct);

            foreach (var p in providers)
            {
                if (!p.Enabled)
                {
                    effective.Providers.Remove(p.Name);
                    continue;
                }

                effective.Providers[p.Name] = new AiProviderOptions
                {
                    Kind = p.Kind,
                    BaseUrl = p.BaseUrl,
                    ApiKey = p.ApiKeyEnc is null ? null : secrets.Unprotect(p.ApiKeyEnc, ApiKeyPurpose),
                };
            }

            foreach (var task in routes.GroupBy(r => r.Task))
            {
                effective.Routes[task.Key] = [.. task.Select(r => new AiRouteOptions
                {
                    Provider = r.Provider, Model = r.Model, Temperature = r.Temperature, MaxOutputTokens = r.MaxOutputTokens, TimeoutSeconds = r.TimeoutSeconds,
                })];
                foreach (var r in task.Where(r => r.InputPricePerMillion is not null || r.OutputPricePerMillion is not null))
                {
                    effective.Prices[$"{r.Provider}/{r.Model}"] = new AiPrice { InputPerMillion = r.InputPricePerMillion ?? 0, OutputPerMillion = r.OutputPricePerMillion ?? 0 };
                }
            }

            foreach (var v in versions)
            {
                if (!effective.Prompts.TryGetValue(v.Key, out var byVersion))
                {
                    effective.Prompts[v.Key] = byVersion = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                byVersion[v.Version] = v.Text;
            }

            foreach (var (key, version) in activations)
            {
                effective.ActivePrompts[key] = version;
            }

            return new Snapshot(
                effective,
                providers.Select(p => p.Name).ToHashSet(StringComparer.Ordinal),
                routes.Select(r => r.Task).ToHashSet(StringComparer.Ordinal),
                activations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep serving the last good settings (or configuration) when the database is unreachable.
            logger.LogWarning(ex, "Could not load the AI settings from the database; using the previous settings");
            return _snapshot ?? new Snapshot(effective, new HashSet<string>(), new HashSet<string>(), new Dictionary<string, string>());
        }
    }

    private static AiOptions Clone(AiOptions o) => new()
    {
        Providers = new(o.Providers.ToDictionary(kv => kv.Key, kv => new AiProviderOptions { Kind = kv.Value.Kind, BaseUrl = kv.Value.BaseUrl, ApiKey = kv.Value.ApiKey }), StringComparer.Ordinal),
        Routes = new(o.Routes.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), StringComparer.Ordinal),
        Prices = new(o.Prices, StringComparer.Ordinal),
        Prompts = new(o.Prompts.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value, StringComparer.Ordinal)), StringComparer.Ordinal),
        ActivePrompts = new(o.ActivePrompts, StringComparer.Ordinal),
        CircuitBreakerFailures = o.CircuitBreakerFailures,
        CircuitBreakerSeconds = o.CircuitBreakerSeconds,
        EvalGate = o.EvalGate,
    };
}

/// <summary>Reloads <see cref="AiSettingsStore"/> on start and every <see cref="AiSettingsOptions.RefreshSeconds"/>.</summary>
internal sealed class AiSettingsRefresher(AiSettingsStore store, IOptions<AiSettingsOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.RefreshSeconds)));
        do
        {
            await store.RefreshAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
