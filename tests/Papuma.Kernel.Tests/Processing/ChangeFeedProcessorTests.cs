// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Processing;

/// <summary>
/// Integration tests for the change feed engine (ADR-009/010): ordering, gapless
/// snapshot reads, NOTIFY wakeup, retry/poison, rebuild, lag and leader coordination.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeFeedProcessorTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public ChangeFeedProcessorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record FeedDoc(string Id, string Name, string? Status = null);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<FeedDoc>().Build();
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private static string NewHandlerName() => $"h_{Guid.NewGuid():N}";

    /// <summary>Collects delivered changes; optionally fails specific document ids.</summary>
    private sealed class RecordingHandler(string name) : IChangeHandler
    {
        public ConcurrentQueue<ChangeRecord> Received { get; } = new();

        public HashSet<string> FailingDocumentIds { get; } = [];

        public string Name => name;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (FailingDocumentIds.Contains(change.DocumentId))
            {
                throw new InvalidOperationException($"Simulated failure for {change.DocumentId}.");
            }

            Received.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    // A fresh handler starts behind every other test's changes in the shared database;
    // the default batch size makes "one cycle delivers my change" depend on test order.
    private ChangeFeedProcessor CreateProcessor(IChangeHandler handler, ChangeFeedProcessorOptions? options = null) =>
        new(_fixture.DataSource, [handler], options ?? new ChangeFeedProcessorOptions { BatchSize = 100_000 });

    [Fact]
    public async Task DeliversCommittedChanges_InSeqOrder_WithFullRecord()
    {
        var scope = NewTenant();
        var id = NewId();
        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new FeedDoc(id, "Harry"), 0);
            await session.SaveAsync(new FeedDoc(id, "Harald"), 1);
            await session.CommitAsync();
        }

        var handler = new RecordingHandler(NewHandlerName());
        await CreateProcessor(handler).ProcessOnceAsync();

        var records = handler.Received.Where(r => r.DocumentId == id).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal([1L, 2L], records.Select(r => r.Version));
        Assert.True(records[0].Seq < records[1].Seq);
        Assert.Equal(ChangeOperation.Insert, records[0].Operation);
        Assert.Equal(scope.TenantId, records[0].Scope.TenantId);
        Assert.True(records[1].FieldChanged<FeedDoc>(x => x.Name));
        Assert.NotEmpty((string)records[0].Metadata["correlationId"]!);
    }

    [Fact]
    public async Task UncommittedSessionWrites_AreInvisible_ToTheProcessor()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var processor = CreateProcessor(handler);
        var id = NewId();

        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new FeedDoc(id, "Pending"), 0);

        await processor.ProcessOnceAsync();
        Assert.DoesNotContain(handler.Received, r => r.DocumentId == id);

        await session.CommitAsync();
        await processor.ProcessOnceAsync();
        Assert.Contains(handler.Received, r => r.DocumentId == id);
    }

    [Fact]
    public async Task GapTest_LongRunningTransaction_HoldsBackOnlyItself_NothingIsSkipped()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var processor = CreateProcessor(handler);
        var slowId = NewId();
        var fastId = NewId();

        // Slow writer: draws the lower seq first, commits last.
        await using var slowSession = _store.OpenSession(NewTenant());
        await slowSession.SaveAsync(new FeedDoc(slowId, "Slow"), 0);

        // Fast writer commits meanwhile, with a higher seq.
        await using (var fastSession = _store.OpenSession(NewTenant()))
        {
            await fastSession.SaveAsync(new FeedDoc(fastId, "Fast"), 0);
            await fastSession.CommitAsync();
        }

        // Delivery follows causal order (ADR-022): the fast change flows at once, the open
        // transaction holds back only its own rows — under ADR-010 it stalled everything.
        await processor.ProcessOnceAsync();
        Assert.Contains(handler.Received, r => r.DocumentId == fastId);
        Assert.DoesNotContain(handler.Received, r => r.DocumentId == slowId);

        await slowSession.CommitAsync();
        await processor.ProcessOnceAsync();

        // Nothing lost: the slow change arrives after its commit, despite its lower seq.
        var relevant = handler.Received.Where(r => r.DocumentId == slowId || r.DocumentId == fastId).ToList();
        Assert.Equal([fastId, slowId], relevant.Select(r => r.DocumentId));
        Assert.True(relevant[1].Seq < relevant[0].Seq);
    }

    [Fact]
    public async Task NotifyWakeup_DeliversFasterThanThePollInterval()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var processor = CreateProcessor(handler, new ChangeFeedProcessorOptions
        {
            PollInterval = TimeSpan.FromSeconds(30), // deliberately long — NOTIFY must beat it
        });

        using var cts = new CancellationTokenSource();
        var run = processor.RunAsync(cts.Token);
        await Task.Delay(500); // let the loop go idle and LISTEN

        var id = NewId();
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(id, "Wake"), 0);
            await session.CommitAsync(); // sends NOTIFY
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && !handler.Received.Any(r => r.DocumentId == id))
        {
            await Task.Delay(50);
        }

        Assert.Contains(handler.Received, r => r.DocumentId == id);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"NOTIFY wakeup took {sw.Elapsed} — expected well below the 30s poll interval.");

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Rebuild_ResetCheckpoint_ReplaysTheFeed()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var processor = CreateProcessor(handler);
        var id = NewId();
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(id, "Once"), 0);
            await session.CommitAsync();
        }

        await processor.ProcessOnceAsync();
        var firstCount = handler.Received.Count(r => r.DocumentId == id);

        await processor.ResetCheckpointAsync(handler.Name);
        await processor.ProcessOnceAsync();

        Assert.Equal(firstCount * 2, handler.Received.Count(r => r.DocumentId == id));
    }

    [Fact]
    public async Task Poison_AfterMaxAttempts_IsSkipped_FailureStaysRecorded_LaterChangesFlow()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var poisonId = NewId();
        var healthyId = NewId();
        handler.FailingDocumentIds.Add(poisonId);

        var processor = CreateProcessor(handler, new ChangeFeedProcessorOptions
        {
            MaxAttempts = 2,
            // The whole shared-database backlog in one cycle, so each cycle is exactly one
            // attempt — a fresh handler starts at seq 0 behind every other test's changes.
            BatchSize = 100_000,
            BaseRetryDelay = TimeSpan.Zero, // immediate retries for the test
        });

        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(poisonId, "Poison"), 0);
            await session.SaveAsync(new FeedDoc(healthyId, "Healthy"), 0);
            await session.CommitAsync();
        }

        // Attempt 1 + attempt 2 (→ poison) + skip cycle: the healthy change behind the
        // poison must flow once the poison is skipped (stop-the-line until then).
        for (var i = 0; i < 4; i++)
        {
            await processor.ProcessOnceAsync();
        }

        Assert.DoesNotContain(handler.Received, r => r.DocumentId == poisonId);
        Assert.Contains(handler.Received, r => r.DocumentId == healthyId);

        var (attempts, lastError) = await LoadFailureAsync(handler.Name);
        Assert.Equal(2, attempts);
        Assert.Contains("Simulated failure", lastError);
    }

    [Fact]
    public async Task Lag_ReflectsPendingChanges_AndDrainsToZero()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var processor = CreateProcessor(handler);
        await processor.ProcessOnceAsync(); // register + drain pre-existing feed entries

        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(NewId(), "LagOne"), 0);
            await session.SaveAsync(new FeedDoc(NewId(), "LagTwo"), 0);
            await session.CommitAsync();
        }

        var before = Assert.Single(await processor.GetLagAsync());
        Assert.Equal(2, before.Lag);

        await processor.ProcessOnceAsync();

        var after = Assert.Single(await processor.GetLagAsync());
        Assert.Equal(0, after.Lag);
    }

    [Fact]
    public async Task LeaderCoordination_LockedCheckpoint_IsSkippedByOtherInstances()
    {
        var handler = new RecordingHandler(NewHandlerName());
        var processor = CreateProcessor(handler);
        await processor.ProcessOnceAsync(); // ensure checkpoint row exists

        var id = NewId();
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.SaveAsync(new FeedDoc(id, "Contended"), 0);
            await session.CommitAsync();
        }

        // Simulate a second instance holding the handler's checkpoint lock.
        await using (var conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.Transaction = tx;
                lockCmd.CommandText = "SELECT last_seq FROM papuma.checkpoint WHERE handler_name = @name FOR UPDATE";
                lockCmd.Parameters.AddWithValue("name", handler.Name);
                await lockCmd.ExecuteScalarAsync();
            }

            var processed = await processor.ProcessOnceAsync();
            Assert.Equal(0, processed); // skipped, no blocking, no double delivery
        }

        // Lock released → next cycle delivers.
        await processor.ProcessOnceAsync();
        Assert.Contains(handler.Received, r => r.DocumentId == id);
    }

    private async Task<(int Attempts, string LastError)> LoadFailureAsync(string handlerName)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT attempts, last_error FROM papuma.failure WHERE handler_name = @name
            """;
        cmd.Parameters.AddWithValue("name", handlerName);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "expected a failure row");
        return (reader.GetInt32(0), reader.GetString(1));
    }
}
