using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pishkhan.AI;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Agent.Evals;

/// <summary>
/// One line of an eval import file (JSON Lines, UTF-8). Either <c>input</c> or <c>messages</c> (anonymized conversation,
/// last <c>customer</c> turn is the input) is required. Example:
/// <code>{"ref":"clothing-0001","vertical":"clothing","messages":[{"role":"customer","text":"سلام قیمت این مانتو چنده؟"}],"expected":{"intent":"price_question","handoff":false,"facts":["price"]}}</code>
/// </summary>
public sealed record EvalImportLine(
    string? Ref,
    string? Vertical,
    string? Input,
    IReadOnlyList<EvalImportTurn>? Messages,
    EvalImportExpected? Expected);

public sealed record EvalImportTurn(string Role, string Text);

public sealed record EvalImportExpected(string? Intent, bool? Handoff, IReadOnlyList<string>? Facts);

public sealed record EvalImportReport(int Imported, int Updated, IReadOnlyList<string> Errors);

public sealed record EvalRunReport(Guid RunId, int Cases, int Errors, decimal? IntentAccuracy, string? Route);

public sealed class EvalService(AgentDbContext db, IModelRouter router, IPromptRegistry prompts, IClock clock, ILogger<EvalService> logger)
{
    public const string ClassifyPromptKey = "intent.classify";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public async Task<EvalImportReport> ImportAsync(string setName, TextReader reader, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var set = await db.EvalSets.FirstOrDefaultAsync(s => s.Name == setName, ct);
        if (set is null)
        {
            set = new EvalSet { Id = Uuid.NewV7(now), Name = setName };
            db.EvalSets.Add(set);
        }

        var existing = await db.EvalCases.Where(c => c.SetId == set.Id).ToDictionaryAsync(c => c.ExternalRef, StringComparer.Ordinal, ct);
        int imported = 0, updated = 0, lineNo = 0;
        var errors = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } raw)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            EvalImportLine? line;
            try
            {
                line = JsonSerializer.Deserialize<EvalImportLine>(raw, Json);
            }
            catch (JsonException ex)
            {
                errors.Add($"line {lineNo}: {ex.Message}");
                continue;
            }

            var input = line?.Input ?? line?.Messages?.LastOrDefault(m => string.Equals(m.Role, "customer", StringComparison.OrdinalIgnoreCase))?.Text;
            if (line is null || string.IsNullOrWhiteSpace(input))
            {
                errors.Add($"line {lineNo}: no input or customer message");
                continue;
            }

            var reference = line.Ref ?? $"line-{lineNo}";
            var context = line.Messages is { Count: > 1 } turns ? JsonSerializer.Serialize(turns.Take(turns.Count - 1), Json) : null;
            if (existing.TryGetValue(reference, out var current))
            {
                current.Input = input;
                current.Vertical = line.Vertical;
                current.ContextJson = context;
                current.ExpectedIntent = line.Expected?.Intent;
                current.ExpectedHandoff = line.Expected?.Handoff;
                current.ExpectedFacts = [.. line.Expected?.Facts ?? []];
                updated++;
            }
            else
            {
                var created = new EvalCase
                {
                    Id = Uuid.NewV7(now),
                    SetId = set.Id,
                    ExternalRef = reference,
                    Input = input,
                    Vertical = line.Vertical,
                    ContextJson = context,
                    ExpectedIntent = line.Expected?.Intent,
                    ExpectedHandoff = line.Expected?.Handoff,
                    ExpectedFacts = [.. line.Expected?.Facts ?? []],
                };
                db.EvalCases.Add(created);
                existing[reference] = created;
                imported++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new EvalImportReport(imported, updated, errors);
    }

    /// <summary>Runs the intent classifier over every case of a set and stores per-case results and the run summary.</summary>
    public async Task<EvalRunReport> RunClassifyAsync(string setName, string? promptVersion, CancellationToken ct)
    {
        var set = await db.EvalSets.AsNoTracking().FirstOrDefaultAsync(s => s.Name == setName, ct)
            ?? throw new InvalidOperationException($"Eval set '{setName}' does not exist.");
        var prompt = prompts.Get(ClassifyPromptKey, promptVersion);
        var cases = await db.EvalCases.AsNoTracking().Where(c => c.SetId == set.Id).OrderBy(c => c.ExternalRef).ToListAsync(ct);
        var run = new EvalRun
        {
            Id = Uuid.NewV7(clock.UtcNow),
            SetId = set.Id,
            Task = AiTasks.Classify,
            PromptKey = prompt.Key,
            PromptVersion = prompt.Version,
            StartedAt = clock.UtcNow,
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var client = router.GetChatClient(AiTasks.Classify);
        var routes = new Dictionary<string, int>(StringComparer.Ordinal);
        int scored = 0, correct = 0;
        foreach (var c in cases)
        {
            var started = Stopwatch.GetTimestamp();
            string? output = null, predicted = null, error = null, route = null;
            try
            {
                var response = await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, prompt.Text), new ChatMessage(ChatRole.User, PersianNormalizer.Normalize(c.Input))],
                    new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                    ct);
                output = response.Text;
                route = response.AdditionalProperties?.TryGetValue("pishkhan.route", out var r) == true ? r?.ToString() : null;
                predicted = ReadIntent(output);
                run.InputTokens += response.Usage?.InputTokenCount ?? 0;
                run.OutputTokens += response.Usage?.OutputTokenCount ?? 0;
            }
            catch (Exception ex) when (ex is AllRoutesFailedException or NoRouteException or JsonException)
            {
                error = ex.Message;
                run.ErrorCount++;
                logger.LogWarning("Eval case {Case} failed: {Error}", c.ExternalRef, ex.Message);
            }

            bool? isCorrect = c.ExpectedIntent is null || error is not null ? null : string.Equals(predicted, c.ExpectedIntent, StringComparison.Ordinal);
            if (isCorrect is { } ok)
            {
                scored++;
                correct += ok ? 1 : 0;
            }

            if (route is not null)
            {
                routes[route] = routes.GetValueOrDefault(route) + 1;
            }

            db.EvalResults.Add(new EvalResult
            {
                Id = Uuid.NewV7(clock.UtcNow),
                RunId = run.Id,
                CaseId = c.Id,
                Output = output is { Length: > 8000 } ? output[..8000] : output,
                PredictedIntent = predicted,
                IntentCorrect = isCorrect,
                Route = route,
                LatencyMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Error = error is { Length: > 1000 } ? error[..1000] : error,
            });
        }

        run.CaseCount = cases.Count;
        run.IntentAccuracy = scored == 0 ? null : decimal.Round((decimal)correct / scored, 4);
        run.Route = routes.Count == 0 ? null : routes.MaxBy(kv => kv.Value).Key;
        run.Status = "completed";
        run.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new EvalRunReport(run.Id, run.CaseCount, run.ErrorCount, run.IntentAccuracy, run.Route);
    }

    private static string? ReadIntent(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var start = output.IndexOf('{', StringComparison.Ordinal);
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(output.AsMemory(start, end - start + 1));
        return doc.RootElement.TryGetProperty("intent", out var intent) ? intent.GetString() : null;
    }
}
