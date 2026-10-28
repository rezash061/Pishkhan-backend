using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pishkhan.Infrastructure.Hosting;
using Pishkhan.Modules.Agent;
using Pishkhan.Modules.Agent.Evals;

// Usage:
//   import <set-name> <file.jsonl>      import or update cases (anonymized, consented conversations)
//   run <set-name> [prompt-version]     run the intent classifier over a set and store the results
// The model route comes from configuration (Ai:Routes:classify), so switching models needs no code change.
if (args.Length < 2 || args[0] is not ("import" or "run"))
{
    await Console.Error.WriteLineAsync("Usage: import <set> <file.jsonl> | run <set> [prompt-version]");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args.Skip(3).ToArray());
builder.AddPishkhanPlatform("pishkhan-eval-runner");
builder.Services.AddAgentModule(builder.Configuration);
using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
var evals = scope.ServiceProvider.GetRequiredService<EvalService>();
using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));

if (args[0] == "import")
{
    if (args.Length < 3)
    {
        await Console.Error.WriteLineAsync("import needs a file");
        return 2;
    }

    using var reader = new StreamReader(args[2], System.Text.Encoding.UTF8);
    var report = await evals.ImportAsync(args[1], reader, cts.Token);
    Console.WriteLine($"imported {report.Imported}, updated {report.Updated}, errors {report.Errors.Count}");
    foreach (var error in report.Errors)
    {
        Console.WriteLine("  " + error);
    }

    return report.Errors.Count == 0 ? 0 : 1;
}

var run = await evals.RunClassifyAsync(args[1], args.Length > 2 ? args[2] : null, cts.Token);
Console.WriteLine($"run {run.RunId}: {run.Cases} cases, {run.Errors} errors, intent accuracy {run.IntentAccuracy?.ToString("P1", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}, route {run.Route}");
return run.Errors == 0 ? 0 : 1;
