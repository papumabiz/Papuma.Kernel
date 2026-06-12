// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Diagnostics;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

using Testcontainers.PostgreSql;

namespace Papuma.Kernel.Benchmarks;

/// <summary>
/// The feed-throughput baseline (implementation plan, post-1.0 prep item): measures
/// write throughput and feed drain rates against a real PostgreSQL 18 — the numbers
/// behind the three scaling triggers (concepts §14). Run: <c>dotnet run -c Release -- feed</c>.
/// </summary>
/// <remarks>
/// Not a BenchmarkDotNet microbenchmark on purpose: the subject is an end-to-end
/// pipeline including the database. One warm measured pass per scenario gives the
/// magnitude the triggers need; micro-precision would be false precision here.
/// All drain scenarios replay the same seeded feed via fresh handler names
/// (checkpoints start at 0) — identical input for every scenario.
/// </remarks>
public static class FeedThroughputProbe
{
    private const int DocumentCount = 10_000;
    private const int Writers = 4;
    private const int SavesPerCommit = 50;

    private sealed record ProbeDoc(string Id, string Name, int Counter, string Status);

    public static async Task RunAsync()
    {
        Console.WriteLine($"Feed throughput probe — {DocumentCount:N0} documents, PostgreSQL 18 (Testcontainers)");

        await using var container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .Build();
        await container.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());

        var model = new KernelModelBuilder().Document<ProbeDoc>().Build();
        await SchemaManager.EnsureSchemaAsync(dataSource, model);
        await CreateProjectionTableAsync(dataSource);
        var store = new DocumentStore(dataSource, model);
        var scope = ScopeContext.Tenant("probe");

        var results = new List<(string Scenario, int Delivered, TimeSpan Elapsed, string Unit)>();

        // ── Phase W: write throughput (seeds the feed for all drain scenarios) ────
        var writeElapsed = await SeedAsync(store, scope);
        results.Add(($"writers: {Writers} parallel sessions, {SavesPerCommit} saves/commit",
            DocumentCount, writeElapsed, "writes/s"));

        // ── Drain scenarios: same feed, fresh checkpoints per scenario ────────────
        results.Add(await DrainAsync(dataSource, "1 no-op handler (engine ceiling)",
            [new NoopHandler("noop-a")]));
        results.Add(await DrainAsync(dataSource, "4 no-op handlers (read amplification)",
            [.. Enumerable.Range(0, 4).Select(i => (IChangeHandler)new NoopHandler($"noop-b{i}"))]));
        results.Add(await DrainAsync(dataSource, "1 projection handler (1 SQL upsert/change)",
            [new ProjectionHandler("proj-a", dataSource)]));
        results.Add(await DrainAsync(dataSource, "4 projection handlers (latency coupling)",
            [.. Enumerable.Range(0, 4).Select(i => (IChangeHandler)new ProjectionHandler($"proj-b{i}", dataSource))]));

        Console.WriteLine();
        Console.WriteLine("| Scenario | Delivered | Elapsed | Rate |");
        Console.WriteLine("|---|---:|---:|---:|");
        foreach (var (scenario, delivered, elapsed, unit) in results)
        {
            Console.WriteLine(
                $"| {scenario} | {delivered:N0} | {elapsed.TotalSeconds:F1} s | {delivered / elapsed.TotalSeconds:N0} {unit} |");
        }
    }

    private static async Task<TimeSpan> SeedAsync(DocumentStore store, ScopeContext scope)
    {
        var perWriter = DocumentCount / Writers;
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Writers).Select(async writer =>
        {
            for (var offset = 0; offset < perWriter; offset += SavesPerCommit)
            {
                await using var session = store.OpenSession(scope);
                for (var i = 0; i < SavesPerCommit; i++)
                {
                    var n = writer * perWriter + offset + i;
                    await session.SaveAsync(
                        new ProbeDoc($"doc-{n:D6}", $"Document {n}", n, "active"), expectedVersion: 0);
                }

                await session.CommitAsync();
            }
        }));
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static async Task<(string, int, TimeSpan, string)> DrainAsync(
        NpgsqlDataSource dataSource, string scenario, IChangeHandler[] handlers)
    {
        using var processor = new ChangeFeedProcessor(dataSource, handlers);
        await processor.ProcessOnceAsync(); // register checkpoints + warm connections

        // Checkpoints were created at 0 by registration, but the warmup cycle already
        // delivered one batch — reset for a clean full-feed measurement.
        foreach (var handler in handlers)
        {
            await processor.ResetCheckpointAsync(handler.Name);
        }

        var delivered = 0;
        var stopwatch = Stopwatch.StartNew();
        int cycle;
        while ((cycle = await processor.ProcessOnceAsync()) > 0)
        {
            delivered += cycle;
        }

        stopwatch.Stop();
        Console.WriteLine($"  drained: {scenario} — {delivered:N0} in {stopwatch.Elapsed.TotalSeconds:F1} s");
        return (scenario, delivered, stopwatch.Elapsed, "deliveries/s");
    }

    private static async Task CreateProjectionTableAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS probe_projection
            (
                handler  text   NOT NULL,
                id       text   NOT NULL,
                version  bigint NOT NULL,
                name     text   NOT NULL,
                PRIMARY KEY (handler, id)
            )
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class NoopHandler(string name) : IChangeHandler
    {
        public string Name => name;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>The realistic baseline: one idempotent SQL upsert per change (concepts §14).</summary>
    private sealed class ProjectionHandler(string name, NpgsqlDataSource dataSource) : IChangeHandler
    {
        public string Name => name;

        public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO probe_projection (handler, id, version, name)
                VALUES (@handler, @id, @version, @name)
                ON CONFLICT (handler, id) DO UPDATE
                SET version = excluded.version, name = excluded.name
                WHERE probe_projection.version < excluded.version
                """;
            cmd.Parameters.AddWithValue("handler", Name);
            cmd.Parameters.AddWithValue("id", change.DocumentId);
            cmd.Parameters.AddWithValue("version", change.Version);
            cmd.Parameters.AddWithValue("name",
                (string?)change.Diff.Entries.GetValueOrDefault("name")?.New ?? string.Empty);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
