// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Review L-5, decision D-7: a session takes the file's write lock with its first operation, so a session kept open
/// over user interaction blocked every writer — the feed worker's outcome write failed after the busy timeout and the
/// batch was delivered again. A read session (<see cref="SqliteDocumentStore.OpenReadSession"/>) takes no write lock,
/// and the feed says when a batch will be redelivered. A database of its own: the feed tests drain the whole feed, and
/// the shared one holds deliberately unmappable rows of other tests.
/// </summary>
public sealed class SqliteReadSessionTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture = new();
    private SqliteDocumentStore _store = null!;

    private sealed record ReadDoc(string Id, string Name);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _store = await _fixture.CreateStoreAsync(new KernelModelBuilder().Document<ReadDoc>().Build());
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private async Task<(ScopeContext Scope, string Id)> SeedAsync()
    {
        var scope = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        await using var session = _store.OpenSession(scope);
        await session.SaveAsync(new ReadDoc(id, "Ann"), 0);
        await session.CommitAsync();
        return (scope, id);
    }

    private SqliteChangeFeedProcessor Processor(IChangeHandler handler, CapturingLogger? logger = null) =>
        new(_fixture.ConnectionString, [handler], notifier: null, new ChangeFeedProcessorOptions { MaxAttempts = 1 }, logger);

    [Fact]
    public async Task AReadSession_ReadsLikeASession()
    {
        var (scope, id) = await SeedAsync();

        await using var session = _store.OpenReadSession(scope);

        Assert.Equal("Ann", (await session.LoadAsync<ReadDoc>(id))!.Document.Name);
        Assert.Single(await session.GetHistoryAsync<ReadDoc>(id));
    }

    [Fact]
    public void AReadSession_HasNoWriteMethod_AWriteIsACompileError()
    {
        var methods = typeof(SqliteReadSession).GetMethods().Select(m => m.Name).ToHashSet();

        foreach (var write in new[]
                 {
                     "SaveAsync", "DeleteAsync", "PatchAsync", "PatchManyAsync", "PatchWhereAsync", "DeleteManyAsync",
                     "DeleteWhereAsync", "AppendAsync", "RedactHistoryAsync", "RedactEventsAsync", "RollbackAsync",
                     "CommitAsync",
                 })
        {
            Assert.DoesNotContain(write, methods);
        }

        // Every read of the write session is there.
        var reads = typeof(SqliteDocumentSession).GetMethods()
            .Where(m => m.DeclaringType == typeof(SqliteDocumentSession) && (m.Name.StartsWith("Load", StringComparison.Ordinal) || m.Name.StartsWith("Get", StringComparison.Ordinal)))
            .Select(m => m.Name);
        Assert.All(reads, name => Assert.Contains(name, methods));
    }

    [Fact]
    public async Task AnOpenReadSession_DoesNotBlockTheFeedWorker()
    {
        var handler = new RecordingHandler($"h_{Guid.NewGuid():N}");
        using var processor = Processor(handler);
        await processor.ProcessOnceAsync(); // registers the handler and drains the shared feed
        var (scope, id) = await SeedAsync();

        await using var session = _store.OpenReadSession(scope);
        await session.LoadAsync<ReadDoc>(id); // the "form is open" moment

        var delivered = await processor.ProcessOnceAsync(); // under a write session: busy timeout, then failure

        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task AnOpenWriteSession_MakesThePhaseThreeWriteFail_AndTheFeedSaysTheBatchIsRedelivered()
    {
        var handler = new RecordingHandler($"h_{Guid.NewGuid():N}");
        var logger = new CapturingLogger();
        using var processor = Processor(handler, logger);
        await processor.ProcessOnceAsync(); // registers the handler and drains the shared feed
        var (scope, id) = await SeedAsync();
        var before = handler.Seen.Count;

        await using (var session = _store.OpenSession(scope))
        {
            await session.LoadAsync<ReadDoc>(id); // a write session holds the lock from here

            await Assert.ThrowsAnyAsync<Exception>(() => processor.ProcessOnceAsync());
        }

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("redelivered", StringComparison.Ordinal));
        Assert.Contains(handler.Name, warning.Message, StringComparison.Ordinal);
        Assert.Contains("OpenReadSession", warning.Message, StringComparison.Ordinal);

        // Delivered once, not checkpointed: the next cycle hands the same record to the handler again.
        Assert.Equal(before + 1, handler.Seen.Count);
        Assert.Equal(1, await processor.ProcessOnceAsync());
        Assert.Equal(before + 2, handler.Seen.Count);
        Assert.Equal(handler.Seen.ElementAt(before), handler.Seen.ElementAt(before + 1));
    }

    [Fact]
    public async Task AWriteBlockedByAnotherSession_FailsAfterTheBusyTimeout_NotAfterHalfAMinute()
    {
        var (scope, id) = await SeedAsync();
        await using var holder = _store.OpenSession(scope);
        await holder.LoadAsync<ReadDoc>(id); // holds the write lock

        await using var blocked = _store.OpenSession(scope);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Microsoft.Data.Sqlite.SqliteException>(() => blocked.LoadAsync<ReadDoc>(id));
        watch.Stop();

        // busy_timeout is 5 s; Microsoft.Data.Sqlite used to retry until its 30 s command timeout on top of it.
        Assert.Equal(5, ex.SqliteErrorCode); // SQLITE_BUSY
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(15));
    }

    private sealed class RecordingHandler(string name) : IChangeHandler
    {
        public string Name => name;

        public ConcurrentQueue<long> Seen { get; } = new();

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            Seen.Enqueue(change.Seq);
            return Task.CompletedTask;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<SqliteChangeFeedProcessor>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }
}
