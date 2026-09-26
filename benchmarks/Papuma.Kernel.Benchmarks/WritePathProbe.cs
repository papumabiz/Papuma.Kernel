// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;

namespace Papuma.Kernel.Benchmarks;

/// <summary>
/// The write-path storage probe (concepts §14, jejak feedback F-14): what declared keys
/// (lost HOT updates), document size, concurrency, fillfactor and TOAST compression cost
/// per save against a real PostgreSQL 18. Decides the deferred key-side-table trigger
/// ("lost HOT costs more than 20 % of saves/s at realistic shape").
/// Run: <c>dotnet run -c Release -- writepath [seconds] [--rounds N] [--durable] [--filter text]</c>.
/// </summary>
/// <remarks>
/// Every scenario gets a fresh container, so HOT ratios, table sizes and WAL are not
/// polluted by an earlier scenario. Each session writes only its own documents (no row
/// contention) with one save per commit — the shape of a request handler. Payloads are
/// random base64: incompressible, so TOAST cannot hide the document size the way
/// repeated characters would. By default the database runs with
/// <c>synchronous_commit = off</c>: the commit fsync is a per-save constant that dilutes
/// the storage cost under test, so the default run shows the upper bound of the relative
/// key cost;
/// <c>--durable</c> keeps the fsync for comparison. With <c>--rounds N</c> every
/// scenario runs N times (fresh container each) and the median round by saves/s counts;
/// <c>--filter</c> keeps the scenarios whose description contains the text
/// (e.g. <c>"Save 5 KB"</c>) — for re-measuring single cells.
/// </remarks>
public static class WritePathProbe
{
    private const int DocumentsPerSession = 25;
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1);

    private sealed record ProbeDoc(
        string Id, string Code, string Category, string OwnerId, int Number, int Counter, string Payload);

    /// <summary>A document owned by exactly one session, with its current version.</summary>
    private sealed class DocumentSlot(ProbeDoc document)
    {
        public ProbeDoc Document { get; set; } = document;

        public long Version { get; set; } = 1;
    }

    private enum WriteKind
    {
        Patch,
        Save,
    }

    private sealed record Scenario(
        int SizeKb, int Keys, int Sessions, WriteKind Kind, int FillFactor = 100, string Compression = "pglz");

    private sealed record Result(
        Scenario Scenario, int Saves, double SavesPerSecond, double P50Ms, double P95Ms,
        double WalBytesPerSave, double HotRatio, long TableGrowth, long IndexGrowth);

    private sealed record Snapshot(long Updates, long HotUpdates, long TableBytes, long IndexBytes, string WalLsn);

    public static async Task RunAsync(string[] args)
    {
        var measure = TimeSpan.FromSeconds(4);
        var rounds = 1;
        var durable = false;
        string? filter = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--durable":
                    durable = true;
                    break;
                case "--filter":
                    filter = args[++i];
                    break;
                case "--rounds":
                    rounds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1, "--rounds");
                    break;
                default:
                    measure = TimeSpan.FromSeconds(int.Parse(args[i], CultureInfo.InvariantCulture));
                    break;
            }
        }

        var scenarios = BuildScenarios()
            .Where(s => filter is null || Describe(s).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.WriteLine(
            $"Write-path probe — {scenarios.Count} scenarios × {rounds} round(s), fresh PostgreSQL 18 container each, " +
            $"{Warmup.TotalSeconds:F0} s warmup + {measure.TotalSeconds:F0} s measured, " +
            $"{DocumentsPerSession} documents per session, synchronous_commit {(durable ? "on" : "off")}");

        var results = new List<Result>();
        foreach (var scenario in scenarios)
        {
            var roundResults = new List<Result>();
            for (var round = 0; round < rounds; round++)
            {
                roundResults.Add(await RunScenarioAsync(scenario, measure, durable));
            }

            var result = roundResults.OrderBy(r => r.SavesPerSecond).ElementAt(rounds / 2);
            results.Add(result);
            Console.WriteLine(
                $"  {Describe(scenario)}: {result.SavesPerSecond:N0} saves/s " +
                $"(rounds: {string.Join(" / ", roundResults.Select(r => r.SavesPerSecond.ToString("N0", CultureInfo.InvariantCulture)))}), " +
                $"HOT {result.HotRatio:P0}");
        }

        PrintResults(results);
        PrintKeyCost(results);
    }

    /// <summary>
    /// The full size × keys × sessions × write-kind matrix at default storage settings,
    /// plus the storage knobs (fillfactor, TOAST compression) at one realistic point —
    /// varied one at a time, for both key variants.
    /// </summary>
    private static List<Scenario> BuildScenarios()
    {
        var scenarios = new List<Scenario>();
        foreach (var kind in new[] { WriteKind.Patch, WriteKind.Save })
        foreach (var sizeKb in new[] { 5, 20, 50 })
        foreach (var sessions in new[] { 1, 16, 32 })
        foreach (var keys in new[] { 0, 3 })
        {
            scenarios.Add(new Scenario(sizeKb, keys, sessions, kind));
        }

        foreach (var keys in new[] { 0, 3 })
        {
            scenarios.Add(new Scenario(20, keys, 16, WriteKind.Patch, FillFactor: 90));
            scenarios.Add(new Scenario(20, keys, 16, WriteKind.Patch, Compression: "lz4"));
        }

        return scenarios;
    }

    private static KernelModel BuildModel(int keys) => new KernelModelBuilder()
        .Document<ProbeDoc>(type =>
        {
            if (keys > 0)
            {
                type.UniqueKey(x => x.Code)
                    .LookupKey(x => x.Category)
                    .UniqueKey(x => new { x.OwnerId, x.Number });
            }
        })
        .Build();

    private static async Task<Result> RunScenarioAsync(Scenario scenario, TimeSpan measure, bool durable)
    {
        await using var db = await PapumaTestDatabase.StartAsync();

        var database = new NpgsqlConnectionStringBuilder(db.OwnerConnectionString).Database;
        await ExecuteAsync(db.OwnerDataSource,
            $"ALTER DATABASE \"{database}\" SET default_toast_compression = '{scenario.Compression}'");
        await ExecuteAsync(db.OwnerDataSource,
            $"ALTER DATABASE \"{database}\" SET synchronous_commit = {(durable ? "on" : "off")}");
        // Database settings apply to new connections only.
        db.OwnerDataSource.Clear();
        db.AppDataSource.Clear();

        var store = await db.CreateStoreAsync(BuildModel(scenario.Keys));
        var scope = ScopeContext.Tenant("probe");
        var documents = await SeedAsync(store, scope, scenario);

        if (scenario.FillFactor != 100)
        {
            // Experiment only: the kernel's schema keeps the default fillfactor.
            await ExecuteAsync(db.OwnerDataSource,
                $"ALTER TABLE papuma.document SET (fillfactor = {scenario.FillFactor})");
            await ExecuteAsync(db.OwnerDataSource, "VACUUM FULL papuma.document");
        }

        var warmup = NewLatencyLists(scenario.Sessions);
        await WriteAsync(store, scope, scenario, documents, Warmup, warmup);
        var before = await SnapshotAsync(db, expectedUpdates: warmup.Sum(l => l.Count));

        var latencies = NewLatencyLists(scenario.Sessions);
        var elapsed = await WriteAsync(store, scope, scenario, documents, measure, latencies);
        var saves = latencies.Sum(l => l.Count);
        var after = await SnapshotAsync(db, expectedUpdates: before.Updates + saves);

        var walBytes = await ScalarAsync<decimal>(db.OwnerDataSource,
            $"SELECT pg_wal_lsn_diff('{after.WalLsn}', '{before.WalLsn}')");
        var sorted = latencies.SelectMany(l => l).Order().ToArray();
        var updates = after.Updates - before.Updates;

        return new Result(
            scenario,
            saves,
            saves / elapsed.TotalSeconds,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            (double)walBytes / saves,
            updates == 0 ? 0 : (double)(after.HotUpdates - before.HotUpdates) / updates,
            after.TableBytes - before.TableBytes,
            after.IndexBytes - before.IndexBytes);
    }

    private static async Task<DocumentSlot[][]> SeedAsync(DocumentStore store, ScopeContext scope, Scenario scenario)
    {
        var documents = new DocumentSlot[scenario.Sessions][];
        for (var s = 0; s < scenario.Sessions; s++)
        {
            await using var session = store.OpenSession(scope);
            documents[s] = new DocumentSlot[DocumentsPerSession];
            for (var d = 0; d < DocumentsPerSession; d++)
            {
                var n = s * DocumentsPerSession + d;
                var doc = new ProbeDoc(
                    $"doc-{n:D5}", $"code-{n:D5}", $"category-{n % 7}", $"owner-{s:D3}", d, 0,
                    RandomPayload(scenario.SizeKb * 1024));
                await session.SaveAsync(doc, expectedVersion: 0);
                documents[s][d] = new DocumentSlot(doc);
            }

            await session.CommitAsync();
        }

        return documents;
    }

    private static List<double>[] NewLatencyLists(int sessions) =>
        [.. Enumerable.Range(0, sessions).Select(_ => new List<double>())];

    /// <summary>Random base64 of roughly <paramref name="bytes"/> characters — incompressible for pglz and lz4.</summary>
    private static string RandomPayload(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes * 3 / 4));

    /// <summary>
    /// Runs every session for <paramref name="duration"/>: round-robin over its own
    /// documents, one write per commit. Returns the wall time of the whole run.
    /// </summary>
    private static async Task<TimeSpan> WriteAsync(
        DocumentStore store, ScopeContext scope, Scenario scenario, DocumentSlot[][] documents,
        TimeSpan duration, List<double>[] latencies)
    {
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, scenario.Sessions).Select(async s =>
        {
            var own = documents[s];
            for (var i = 0; stopwatch.Elapsed < duration; i++)
            {
                var slot = own[i % own.Length];
                var changed = slot.Document with { Counter = slot.Document.Counter + 1 };
                var started = Stopwatch.GetTimestamp();
                await using (var session = store.OpenSession(scope))
                {
                    if (scenario.Kind == WriteKind.Patch)
                    {
                        await session.PatchAsync<ProbeDoc>(changed.Id, p => p.Increment(x => x.Counter));
                    }
                    else
                    {
                        await session.SaveAsync(changed, expectedVersion: slot.Version);
                    }

                    await session.CommitAsync();
                }

                latencies[s].Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                slot.Document = changed;
                slot.Version++;
            }
        }));
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    /// <summary>
    /// Reads the table statistics once every backend has reported its updates. Backends
    /// flush their pending statistics on exit, so the pools are cleared first; the counters
    /// are then polled until they cover <paramref name="expectedUpdates"/>.
    /// </summary>
    private static async Task<Snapshot> SnapshotAsync(PapumaTestDatabase db, long expectedUpdates)
    {
        db.AppDataSource.Clear();
        db.OwnerDataSource.Clear();

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            await using var conn = await db.OwnerDataSource.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT n_tup_upd, n_tup_hot_upd,
                       pg_table_size('papuma.document'), pg_indexes_size('papuma.document'),
                       pg_current_wal_lsn()::text
                FROM pg_stat_user_tables
                WHERE schemaname = 'papuma' AND relname = 'document'
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            await reader.ReadAsync();
            var snapshot = new Snapshot(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
            if (snapshot.Updates >= expectedUpdates || deadline.Elapsed > TimeSpan.FromSeconds(15))
            {
                return snapshot;
            }

            await Task.Delay(100);
        }
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

    private static void PrintResults(List<Result> results)
    {
        Console.WriteLine();
        Console.WriteLine("| Write | Size | Keys | Sessions | Fillfactor | TOAST | Saves/s | p50 | p95 | WAL/save | HOT | Table +MB | Index +MB |");
        Console.WriteLine("|---|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            var s = r.Scenario;
            Console.WriteLine(
                $"| {s.Kind} | {s.SizeKb} KB | {s.Keys} | {s.Sessions} | {s.FillFactor} | {s.Compression} " +
                $"| {r.SavesPerSecond:N0} | {r.P50Ms:F1} ms | {r.P95Ms:F1} ms | {r.WalBytesPerSave / 1024:F1} KB " +
                $"| {r.HotRatio:P0} | {r.TableGrowth / 1048576.0:F1} | {r.IndexGrowth / 1048576.0:F2} |");
        }
    }

    /// <summary>The trigger view: saves/s with 3 declared keys relative to none, same shape otherwise.</summary>
    private static void PrintKeyCost(List<Result> results)
    {
        Console.WriteLine();
        Console.WriteLine("| Write | Size | Sessions | Fillfactor | TOAST | Saves/s, 0 keys | Saves/s, 3 keys | Δ saves/s | Δ WAL/save |");
        Console.WriteLine("|---|---:|---:|---:|---|---:|---:|---:|---:|");
        foreach (var without in results.Where(r => r.Scenario.Keys == 0))
        {
            var with = results.SingleOrDefault(r => r.Scenario == without.Scenario with { Keys = 3 });
            if (with is null)
            {
                continue;
            }

            var s = without.Scenario;
            Console.WriteLine(
                $"| {s.Kind} | {s.SizeKb} KB | {s.Sessions} | {s.FillFactor} | {s.Compression} " +
                $"| {without.SavesPerSecond:N0} | {with.SavesPerSecond:N0} " +
                $"| {with.SavesPerSecond / without.SavesPerSecond - 1:+0%;-0%} " +
                $"| {with.WalBytesPerSave / without.WalBytesPerSave - 1:+0%;-0%} |");
        }
    }

    private static string Describe(Scenario s) =>
        $"{s.Kind} {s.SizeKb} KB, {s.Keys} keys, {s.Sessions} sessions, ff {s.FillFactor}, {s.Compression}";

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
