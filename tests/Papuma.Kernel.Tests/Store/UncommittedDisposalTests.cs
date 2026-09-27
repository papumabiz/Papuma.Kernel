// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// A session disposed with uncommitted writes must not vanish silently (feedback F-18):
/// the rollback is logged and counted; <c>DiscardAsync</c> rolls back on purpose.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UncommittedDisposalTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly CapturingLogger _logger = new();
    private DocumentStore _store = null!;

    public UncommittedDisposalTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record PendingDoc(string Id, string Name);

    private sealed record PendingEvent(string Name);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<PendingDoc>().Event<PendingEvent>().Build();
        await _fixture.Database.EnsureSchemaAsync(model);
        _store = new DocumentStore(_fixture.Database.AppDataSource, model, _logger);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Dispose_WithUncommittedWrites_RollsBack_LogsAWarning_AndCounts()
    {
        using var disposals = new DisposalCounter();
        var scope = ScopeContext.Tenant(Guid.NewGuid());
        var id = NewId();
        Guid correlationId;

        await using (var session = _store.OpenSession(scope))
        {
            correlationId = session.CorrelationId;
            await session.SaveAsync(new PendingDoc(id, "forgotten"), 0);
            await session.SaveAsync(new PendingDoc(NewId(), "also forgotten"), 0);
            await session.AppendAsync(new PendingEvent("forgotten"));
        }

        var entry = Assert.Single(_logger.Entries, e => e.Message.Contains(correlationId.ToString()));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1001, entry.EventId.Id);
        Assert.Contains("3 uncommitted write(s)", entry.Message);
        Assert.Contains($"{nameof(PendingDoc)}/{id}", entry.Message);
        Assert.True(disposals.Count >= 1);

        await using var verify = _store.OpenSession(scope);
        Assert.Null(await verify.LoadAsync<PendingDoc>(id));
    }

    [Fact]
    public async Task DiscardAsync_RollsBackOnPurpose_WithoutWarning_AndTheSessionStaysUsable()
    {
        var scope = ScopeContext.Tenant(Guid.NewGuid());
        var dropped = NewId();
        var kept = NewId();
        Guid correlationId;

        await using (var session = _store.OpenSession(scope))
        {
            correlationId = session.CorrelationId;
            await session.SaveAsync(new PendingDoc(dropped, "dropped"), 0);
            await session.DiscardAsync();

            await session.SaveAsync(new PendingDoc(kept, "kept"), 0);
            await session.CommitAsync();
        }

        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains(correlationId.ToString()));

        await using var verify = _store.OpenSession(scope);
        Assert.Null(await verify.LoadAsync<PendingDoc>(dropped));
        Assert.NotNull(await verify.LoadAsync<PendingDoc>(kept));
    }

    [Fact]
    public async Task Dispose_AfterCommit_OrAfterReadsOnly_IsQuiet()
    {
        var scope = ScopeContext.Tenant(Guid.NewGuid());
        var id = NewId();
        var correlationIds = new List<Guid>();

        await using (var writer = _store.OpenSession(scope))
        {
            correlationIds.Add(writer.CorrelationId);
            await writer.SaveAsync(new PendingDoc(id, "committed"), 0);
            await writer.CommitAsync();
        }

        await using (var reader = _store.OpenSession(scope))
        {
            correlationIds.Add(reader.CorrelationId);
            Assert.NotNull(await reader.LoadAsync<PendingDoc>(id)); // opens a transaction, writes nothing
        }

        Assert.DoesNotContain(_logger.Entries, e => correlationIds.Any(c => e.Message.Contains(c.ToString())));
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLogger : ILogger<DocumentStore>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception)));
    }

    /// <summary>
    /// Counts <c>papuma.session.uncommitted_disposals</c> while alive. The PostgreSQL tests
    /// run serially in one collection, so no other session is disposed meanwhile.
    /// </summary>
    private sealed class DisposalCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;

        public DisposalCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Papuma.Kernel" && instrument.Name == "papuma.session.uncommitted_disposals")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
            _listener.Start();
        }

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }
}
