// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Processing;

/// <summary>
/// The SQLite feed run loops must outlive what goes wrong around them (review L-1, L-2,
/// N-1): a handler's own timeout is a failure and not a shutdown, a database that cannot
/// be opened is answered with backoff and not a busy loop, and one handler's broken batch
/// does not keep the others from running. A database file of its own per test — feed state
/// is global per file.
/// </summary>
public sealed class SqliteFeedWorkerResilienceTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private SqliteDocumentStore _store = null!;

    private sealed record FlowDoc(string Id, string Name);

    private sealed record FlowEvent(string Note);

    // Retries immediately and polls fast: these tests are about the loop, not the delays.
    private static readonly ChangeFeedProcessorOptions FastRetry = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(50),
        BaseRetryDelay = TimeSpan.Zero,
        MaxRetryDelay = TimeSpan.Zero,
    };

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var model = new KernelModelBuilder().Document<FlowDoc>().Event<FlowEvent>().Build();
        _store = await _db.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private async Task WriteDocAsync()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new FlowDoc(Guid.NewGuid().ToString("N"), "x"), 0);
        await session.CommitAsync();
    }

    private async Task WriteEventAsync()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.AppendAsync(new FlowEvent("x"));
        await session.CommitAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Times out on its first call (as an HttpClient does), then behaves.</summary>
    private sealed class TimeoutOnceChangeHandler : IChangeHandler
    {
        private int _calls;
        private int _succeeded;

        public string Name { get; } = $"timeout-{Guid.NewGuid():N}";

        public int Succeeded => Volatile.Read(ref _succeeded);

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            }

            Interlocked.Increment(ref _succeeded);
            return Task.CompletedTask;
        }
    }

    private sealed class TimeoutOnceEventHandler : IEventHandler
    {
        private int _calls;
        private int _succeeded;

        public string Name { get; } = $"timeout-evt-{Guid.NewGuid():N}";

        public int Succeeded => Volatile.Read(ref _succeeded);

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            }

            Interlocked.Increment(ref _succeeded);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingChangeHandler(string name) : IChangeHandler
    {
        private int _delivered;

        public string Name => name;

        public int Delivered => Volatile.Read(ref _delivered);

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            Interlocked.Increment(ref _delivered);
            return Task.CompletedTask;
        }
    }

    private sealed class LogCounter<T> : ILogger<T>
    {
        private int _warningsOrWorse;

        public int WarningsOrWorse => Volatile.Read(ref _warningsOrWorse);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Interlocked.Increment(ref _warningsOrWorse);
            }
        }
    }

    [Fact]
    public async Task ChangeFeed_HandlerTimeout_IsAFailureEntry_NotAShutdown()
    {
        await WriteDocAsync();
        var handler = new TimeoutOnceChangeHandler();
        using var processor = new SqliteChangeFeedProcessor(_db.ConnectionString, [handler], notifier: null, FastRetry);

        await processor.ProcessOnceAsync();

        var failure = Assert.Single(await processor.GetFailuresAsync());
        Assert.Equal(handler.Name, failure.HandlerName);
        Assert.Equal(1, failure.Attempts);
        Assert.Contains("TaskCanceledException", failure.LastError, StringComparison.Ordinal);

        await processor.ProcessOnceAsync();
        Assert.Equal(1, handler.Succeeded);
        Assert.Empty(await processor.GetFailuresAsync());
    }

    [Fact]
    public async Task ChangeFeed_RunLoop_SurvivesAHandlerTimeout_AndDeliversOnRetry()
    {
        await WriteDocAsync();
        var handler = new TimeoutOnceChangeHandler();
        using var processor = new SqliteChangeFeedProcessor(_db.ConnectionString, [handler], notifier: null, FastRetry);
        using var cts = new CancellationTokenSource();

        var run = processor.RunAsync(cts.Token);
        await WaitUntilAsync(() => handler.Succeeded == 1);

        Assert.False(run.IsCompleted, "A handler timeout ended the run loop.");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task EventFeed_RunLoop_SurvivesAHandlerTimeout_AndDeliversOnRetry()
    {
        await WriteEventAsync();
        var handler = new TimeoutOnceEventHandler();
        using var processor = new SqliteEventFeedProcessor(_db.ConnectionString, [handler], notifier: null, FastRetry);
        using var cts = new CancellationTokenSource();

        var run = processor.RunAsync(cts.Token);
        await WaitUntilAsync(() => handler.Succeeded == 1);

        Assert.False(run.IsCompleted, "A handler timeout ended the run loop.");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ChangeFeed_UnopenableDatabase_BacksOff_InsteadOfSpinning()
    {
        var log = new LogCounter<SqliteChangeFeedProcessor>();
        using var processor = new SqliteChangeFeedProcessor(
            MissingDatabase(), [new CountingChangeHandler("h")], notifier: null, FastRetry, log);
        using var cts = new CancellationTokenSource();

        // Task.Run: a regressed loop completes every await synchronously and would never
        // return control here — the test must fail on the log count, not hang.
        var run = Task.Run(() => processor.RunAsync(cts.Token));
        await Task.Delay(TimeSpan.FromSeconds(2));

        // A busy loop logs thousands of lines in 2 s; 50 ms doubling logs a handful.
        Assert.InRange(log.WarningsOrWorse, 1, 12);
        Assert.False(run.IsCompleted, "The loop must keep retrying, not give up.");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task EventFeed_UnopenableDatabase_BacksOff_InsteadOfSpinning()
    {
        var log = new LogCounter<SqliteEventFeedProcessor>();
        using var processor = new SqliteEventFeedProcessor(
            MissingDatabase(), [new TimeoutOnceEventHandler()], notifier: null, FastRetry, log);
        using var cts = new CancellationTokenSource();

        var run = Task.Run(() => processor.RunAsync(cts.Token)); // see the change-feed twin
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.InRange(log.WarningsOrWorse, 1, 12);
        Assert.False(run.IsCompleted, "The loop must keep retrying, not give up.");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ChangeFeed_OneHandlersFaultedBatch_DoesNotBlockTheOtherHandlers()
    {
        var faulty = new CountingChangeHandler($"faulty-{Guid.NewGuid():N}");
        var healthy = new CountingChangeHandler($"healthy-{Guid.NewGuid():N}");
        using var processor = new SqliteChangeFeedProcessor(
            _db.ConnectionString, [faulty, healthy], notifier: null, FastRetry);

        await processor.ProcessOnceAsync(); // registers both checkpoints on the empty feed

        // The faulty handler cannot record its outcome — a stand-in for any batch that cannot
        // be mapped or written, which must stay that handler's problem.
        await ExecuteAsync($"""
            CREATE TRIGGER test_fail_checkpoint BEFORE UPDATE ON checkpoint
            WHEN OLD.handler_name = '{faulty.Name}'
            BEGIN SELECT RAISE(ABORT, 'simulated checkpoint write failure'); END;
            """);
        await WriteDocAsync();

        var ex = await Assert.ThrowsAsync<SqliteException>(() => processor.ProcessOnceAsync());

        Assert.Contains("simulated checkpoint write failure", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, healthy.Delivered);
    }

    // A file that does not exist and must not be created: opening fails at once, every time.
    private string MissingDatabase() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), $"papuma_missing_{Guid.NewGuid():N}.db")};Mode=ReadWrite";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }
}
