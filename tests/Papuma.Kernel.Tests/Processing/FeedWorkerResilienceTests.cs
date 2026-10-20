// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Processing;

/// <summary>
/// The feed run loops must outlive what goes wrong around them (review P-1, P-2, P-10):
/// a handler's own timeout is a failure and not a shutdown, an unreachable database is
/// answered with backoff and not a busy loop, and one handler's broken batch does not
/// keep the others from running.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedWorkerResilienceTests
{
    private readonly PostgresFixture _fixture;

    public FeedWorkerResilienceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record FlowDoc(string Id, string Name);

    private sealed record FlowEvent(string Note);

    private static readonly KernelModel Model = new KernelModelBuilder()
        .Document<FlowDoc>()
        .Event<FlowEvent>()
        .Build();

    // Retries immediately: the delay is the database clock's, and these tests are about the loop.
    private static readonly ChangeFeedProcessorOptions FastRetry = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(50),
        BaseRetryDelay = TimeSpan.Zero,
        MaxRetryDelay = TimeSpan.Zero,
    };

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private async Task<(ScratchDb Db, DocumentStore Store)> NewDatabaseAsync()
    {
        var db = await ScratchDb.CreateAsync(_fixture.Database);
        await SchemaManager.EnsureSchemaAsync(db.DataSource, Model);
        return (db, new DocumentStore(db.DataSource, Model));
    }

    private static async Task WriteDocAsync(DocumentStore store)
    {
        await using var session = store.OpenSession(NewTenant());
        await session.SaveAsync(new FlowDoc(Guid.NewGuid().ToString("N"), "x"), 0);
        await session.CommitAsync();
    }

    private static async Task WriteEventAsync(DocumentStore store)
    {
        await using var session = store.OpenSession(NewTenant());
        await session.AppendAsync(new FlowEvent("x"));
        await session.CommitAsync();
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
        private int _errorsOrWorse;

        public int WarningsOrWorse => Volatile.Read(ref _warningsOrWorse);

        public int ErrorsOrWorse => Volatile.Read(ref _errorsOrWorse);

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

            if (logLevel >= LogLevel.Error)
            {
                Interlocked.Increment(ref _errorsOrWorse);
            }
        }
    }

    [Fact]
    public async Task ChangeFeed_HandlerTimeout_IsAFailureEntry_NotAShutdown()
    {
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        await WriteDocAsync(store);
        var handler = new TimeoutOnceChangeHandler();
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], FastRetry);

        // Before the fix the TaskCanceledException escaped as if the host were stopping.
        await processor.ProcessOnceAsync();

        var failure = Assert.Single(await processor.GetFailuresAsync());
        Assert.Equal(handler.Name, failure.HandlerName);
        Assert.Equal(1, failure.Attempts);
        Assert.Contains("TaskCanceledException", failure.LastError, StringComparison.Ordinal);

        await processor.ProcessOnceAsync(); // retried, delivered, failure entry cleared
        Assert.Equal(1, handler.Succeeded);
        Assert.Empty(await processor.GetFailuresAsync());
    }

    [Fact]
    public async Task ChangeFeed_RunLoop_SurvivesAHandlerTimeout_AndDeliversOnRetry()
    {
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        await WriteDocAsync(store);
        var handler = new TimeoutOnceChangeHandler();
        var log = new LogCounter<ChangeFeedProcessor>();
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], FastRetry, log);
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
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        await WriteEventAsync(store);
        var handler = new TimeoutOnceEventHandler();
        using var processor = new EventFeedProcessor(db.DataSource, [handler], FastRetry);
        using var cts = new CancellationTokenSource();

        var run = processor.RunAsync(cts.Token);
        await WaitUntilAsync(() => handler.Succeeded == 1);

        Assert.False(run.IsCompleted, "A handler timeout ended the run loop.");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ChangeFeed_ShutdownByToken_EndsCleanly_WithoutAnErrorLog()
    {
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        await WriteDocAsync(store);
        var handler = new CountingChangeHandler($"count-{Guid.NewGuid():N}");
        var log = new LogCounter<ChangeFeedProcessor>();
        using var processor = new ChangeFeedProcessor(db.DataSource, [handler], FastRetry, log);
        using var cts = new CancellationTokenSource();

        var run = processor.RunAsync(cts.Token);
        await WaitUntilAsync(() => handler.Delivered == 1);
        await cts.CancelAsync();
        await run;

        Assert.Equal(0, log.ErrorsOrWorse);
    }

    [Fact]
    public async Task ChangeFeed_UnreachableDatabase_BacksOff_InsteadOfSpinning()
    {
        await using var dataSource = NpgsqlDataSource.Create(UnreachableConnectionString());
        var log = new LogCounter<ChangeFeedProcessor>();
        using var processor = new ChangeFeedProcessor(
            dataSource, [new CountingChangeHandler("h")], FastRetry, log);
        using var cts = new CancellationTokenSource();

        // Task.Run: a regressed loop can complete its awaits synchronously and would never
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
    public async Task EventFeed_UnreachableDatabase_BacksOff_InsteadOfSpinning()
    {
        await using var dataSource = NpgsqlDataSource.Create(UnreachableConnectionString());
        var log = new LogCounter<EventFeedProcessor>();
        using var processor = new EventFeedProcessor(
            dataSource, [new TimeoutOnceEventHandler()], FastRetry, log);
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
        var (db, store) = await NewDatabaseAsync();
        await using var _ = db;
        var faulty = new CountingChangeHandler($"faulty-{Guid.NewGuid():N}");
        var healthy = new CountingChangeHandler($"healthy-{Guid.NewGuid():N}");
        using var processor = new ChangeFeedProcessor(db.DataSource, [faulty, healthy], FastRetry);

        await processor.ProcessOnceAsync(); // registers both checkpoints on the empty feed

        // The faulty handler cannot move its checkpoint — a stand-in for a batch that cannot
        // be mapped or written, which must stay that handler's problem.
        await db.ExecuteAsync($"""
            CREATE FUNCTION papuma.test_fail_checkpoint() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'simulated checkpoint write failure'; END $$;
            CREATE TRIGGER test_fail_checkpoint BEFORE UPDATE ON papuma.checkpoint
                FOR EACH ROW WHEN (OLD.handler_name = '{faulty.Name}')
                EXECUTE FUNCTION papuma.test_fail_checkpoint();
            """);
        await WriteDocAsync(store);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => processor.ProcessOnceAsync());

        Assert.Contains("simulated checkpoint write failure", ex.MessageText, StringComparison.Ordinal);
        Assert.Equal(1, healthy.Delivered);
    }

    // A real server that refuses the login at once: unlike a closed port (which Windows only
    // refuses after about a second and so throttles a busy loop by itself) this fails in
    // milliseconds on every platform, which is what makes a missing backoff visible.
    private string UnreachableConnectionString() =>
        new NpgsqlConnectionStringBuilder(_fixture.Database.OwnerConnectionString)
        {
            Password = "definitely-not-the-password",
            Pooling = false,
        }.ConnectionString;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    /// <summary>A database of its own in the shared container: feed state is global per database.</summary>
    private sealed class ScratchDb : IAsyncDisposable
    {
        private readonly PapumaTestDatabase _server;
        private readonly string _name;

        private ScratchDb(PapumaTestDatabase server, string name, NpgsqlDataSource dataSource)
        {
            _server = server;
            _name = name;
            DataSource = dataSource;
        }

        public NpgsqlDataSource DataSource { get; }

        public static async Task<ScratchDb> CreateAsync(PapumaTestDatabase server)
        {
            var name = $"flow_{Guid.NewGuid():N}";
            await using (var cmd = server.OwnerDataSource.CreateCommand($"CREATE DATABASE {name}"))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(server.OwnerConnectionString) { Database = name };
            return new ScratchDb(server, name, NpgsqlDataSource.Create(builder.ConnectionString));
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var cmd = DataSource.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await using var cmd = _server.OwnerDataSource.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
