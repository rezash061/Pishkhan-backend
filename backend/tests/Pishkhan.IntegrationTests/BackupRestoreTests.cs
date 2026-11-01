using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// The backup and restore drill (BE-1.15): <c>infra/backup/backup.sh</c> dumps the live test database while the other tests write to it,
/// <c>restore.sh</c> restores the dump into a new database, and the copy has every schema, migration and index, and the data written
/// before the dump.
/// </summary>
public sealed class BackupRestoreTests(InfraFixture infra) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Backup_restores_into_a_new_database_with_schema_and_data()
    {
        // Data written before the dump.
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var title = "مانتو پشتیبان " + Guid.NewGuid().ToString("N")[..6];
        var created = await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/catalog/products", new { title, status = "active", price = new { amount = 250_000, currency = "IRR" } }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var productId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        foreach (var name in new[] { "backup.sh", "restore.sh", "verify.sql" })
        {
            await infra.CopyToPostgresAsync(await File.ReadAllBytesAsync(Path.Combine(ScriptsDirectory(), name), Ct), "/opt/pishkhan-backup/" + name, Ct);
        }

        string[] Env(params string[] command) => ["env", "PGHOST=localhost", "PGUSER=pishkhan", "PGPASSWORD=pishkhan", "PGDATABASE=pishkhan", .. command];

        var backup = await infra.ExecInPostgresAsync(Env("bash", "/opt/pishkhan-backup/backup.sh", "/tmp/pishkhan-backups"), Ct);
        backup.ExitCode.ShouldBe(0, backup.Stderr);
        var dump = backup.Stdout.Trim();
        dump.ShouldStartWith("/tmp/pishkhan-backups/pishkhan-");

        var target = "restore_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var restore = await infra.ExecInPostgresAsync(Env("bash", "/opt/pishkhan-backup/restore.sh", dump, target), Ct);
            restore.ExitCode.ShouldBe(0, restore.Stderr);
            restore.Stdout.ShouldContain("catalog");

            // Never over an existing database.
            var again = await infra.ExecInPostgresAsync(Env("bash", "/opt/pishkhan-backup/restore.sh", dump, target), Ct);
            again.ExitCode.ShouldBe(2);

            // A damaged dump is refused by its checksum.
            var tampered = await infra.ExecInPostgresAsync(["bash", "-c", $"cp {dump} /tmp/t.dump && cp {dump}.sha256 /tmp/t.dump.sha256 && sed -i 's/{Path.GetFileName(dump)}/t.dump/' /tmp/t.dump.sha256 && printf x >> /tmp/t.dump"], Ct);
            tampered.ExitCode.ShouldBe(0, tampered.Stderr);
            (await infra.ExecInPostgresAsync(Env("bash", "/opt/pishkhan-backup/restore.sh", "/tmp/t.dump", target + "_t"), Ct)).ExitCode.ShouldNotBe(0);

            await using var live = new NpgsqlConnection(infra.PostgresConnectionString);
            await using var copy = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(infra.PostgresConnectionString) { Database = target }.ConnectionString);

            // Same tables, columns, indexes, extensions and migrations.
            const string tables = "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1";
            const string columns = "SELECT table_schema || '.' || table_name || '.' || column_name || ':' || data_type FROM information_schema.columns WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1";
            const string indexes = "SELECT schemaname || '.' || indexname FROM pg_indexes WHERE schemaname NOT IN ('pg_catalog') ORDER BY 1";
            const string extensions = "SELECT extname FROM pg_extension ORDER BY 1";
            foreach (var sql in new[] { tables, columns, indexes, extensions })
            {
                (await copy.QueryAsync<string>(sql)).ToList().ShouldBe((await live.QueryAsync<string>(sql)).ToList(), sql);
            }

            var histories = (await live.QueryAsync<string>("SELECT table_schema FROM information_schema.tables WHERE table_name = '__ef_migrations_history'")).ToList();
            histories.Count.ShouldBeGreaterThan(10);
            foreach (var schema in histories)
            {
                var sql = $"SELECT migration_id FROM \"{schema}\".__ef_migrations_history ORDER BY 1";
                (await copy.QueryAsync<string>(sql)).ToList().ShouldBe((await live.QueryAsync<string>(sql)).ToList(), schema);
            }

            // The data written before the dump.
            (await copy.ExecuteScalarAsync<string>("SELECT title FROM catalog.products WHERE id = @productId", new { productId })).ShouldBe(title);
            (await copy.ExecuteScalarAsync<long>("SELECT count(*) FROM tenancy.workspaces WHERE id = @ws", new { ws })).ShouldBe(1);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(infra.PostgresConnectionString);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {target} WITH (FORCE)");
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {target}_t WITH (FORCE)");
        }
    }

    private static string ScriptsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "infra", "backup");
            if (File.Exists(Path.Combine(candidate, "backup.sh")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("infra/backup not found above " + AppContext.BaseDirectory);
    }
}
