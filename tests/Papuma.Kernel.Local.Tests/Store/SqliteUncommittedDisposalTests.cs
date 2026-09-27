// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Parity with <c>UncommittedDisposalTests</c> (feedback F-18): a dispose with uncommitted
/// writes is logged; <c>DiscardAsync</c> rolls back on purpose and keeps the session usable.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteUncommittedDisposalTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private readonly CapturingLogger _logger = new();
    private SqliteDocumentStore _store = null!;

    public SqliteUncommittedDisposalTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record PendingDoc(string Id, string Name);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<PendingDoc>().Build();
        await _fixture.CreateStoreAsync(model); // applies the schema
        _store = new SqliteDocumentStore(_fixture.ConnectionString, model, notifyWaiters: null, _logger);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Dispose_WithUncommittedWrites_RollsBack_AndLogsAWarning()
    {
        var scope = ScopeContext.Tenant(Guid.NewGuid());
        var id = NewId();
        Guid correlationId;

        await using (var session = _store.OpenSession(scope))
        {
            correlationId = session.CorrelationId;
            await session.SaveAsync(new PendingDoc(id, "forgotten"), 0);
        }

        var entry = Assert.Single(_logger.Entries, e => e.Message.Contains(correlationId.ToString()));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1001, entry.EventId.Id);
        Assert.Contains($"1 uncommitted write(s), the first to {nameof(PendingDoc)}/{id}", entry.Message);

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

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLogger : ILogger<SqliteDocumentStore>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception)));
    }
}
