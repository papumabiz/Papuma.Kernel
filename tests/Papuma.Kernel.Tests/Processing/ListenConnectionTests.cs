// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Processing;

/// <summary>
/// NOTIFY is only a wakeup (ADR-010): when the LISTEN connection dies — failover, server
/// restart, a pooler or an admin killing it — the run loop must keep delivering by
/// polling, without spinning, and pick LISTEN up again.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ListenConnectionTests
{
    private readonly PostgresFixture _fixture;

    public ListenConnectionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record ListenDoc(string Id, int Step);

    private sealed class CountingHandler : IChangeHandler
    {
        private int _delivered;

        public string Name { get; } = $"listen-{Guid.NewGuid():N}";

        public int Delivered => Volatile.Read(ref _delivered);

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            if (change.DocumentType == nameof(ListenDoc))
            {
                Interlocked.Increment(ref _delivered);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class WarningCounter : ILogger<ChangeFeedProcessor>
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Interlocked.Increment(ref _warnings);
            }
        }
    }

    [Fact]
    public async Task KilledListenConnection_DoesNotSpin_AndDeliveryContinues()
    {
        var model = new KernelModelBuilder().Document<ListenDoc>().Build();
        var store = await _fixture.Database.CreateStoreAsync(model);
        var handler = new CountingHandler();
        var log = new WarningCounter();
        var options = new ChangeFeedProcessorOptions { PollInterval = TimeSpan.FromMilliseconds(500) };
        using var processor = new ChangeFeedProcessor(_fixture.DataSource, [handler], options, log);
        using var cts = new CancellationTokenSource();
        var run = processor.RunAsync(cts.Token);

        await WaitUntilAsync(async () => await TerminateListenBackendsAsync() > 0);
        var warningsAtKill = log.Warnings;
        await Task.Delay(TimeSpan.FromSeconds(3));

        // A spinning loop logs hundreds of warnings per second; a polling one a handful.
        Assert.InRange(log.Warnings - warningsAtKill, 0, 20);

        await using (var session = store.OpenSession(ScopeContext.Tenant(Guid.NewGuid())))
        {
            await session.SaveAsync(new ListenDoc(Guid.NewGuid().ToString("N"), 1), 0);
            await session.CommitAsync();
        }

        await WaitUntilAsync(() => Task.FromResult(handler.Delivered == 1));

        // LISTEN is re-established: a NOTIFY wakes the idle wait again.
        Assert.Equal(1, await CountListenBackendsAsync());

        await cts.CancelAsync();
        await run;
    }

    private async Task<long> TerminateListenBackendsAsync()
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity " +
            "WHERE query LIKE 'LISTEN papuma_changes%' AND pid <> pg_backend_pid()");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountListenBackendsAsync()
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE query LIKE 'LISTEN papuma_changes%' AND pid <> pg_backend_pid()");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            await Task.Delay(100, timeout.Token);
        }
    }
}
