// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Hosting;

/// <summary>
/// <see cref="PapumaKernelOptions.RunFeedWorkers"/>: a host-based test fixture turns the
/// hosted feed loops off and drives the feeds with <c>DrainAsync</c> alone (feedback F-23).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedWorkerOptionTests
{
    private readonly PostgresFixture _fixture;

    public FeedWorkerOptionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record WorkerNote(string Id, string Text);

    private sealed record NoteTaken(string NoteId);

    private sealed class CollectingChangeHandler : IChangeHandler
    {
        public ConcurrentQueue<ChangeRecord> Received { get; } = new();

        public string Name { get; } = $"worker_changes_{Guid.NewGuid():N}";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            Received.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private sealed class CollectingEventHandler : IEventHandler
    {
        public ConcurrentQueue<EventRecord> Received { get; } = new();

        public string Name { get; } = $"worker_events_{Guid.NewGuid():N}";

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            Received.Enqueue(@event);
            return Task.CompletedTask;
        }
    }

    private IHost BuildHost(bool runFeedWorkers)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddPapumaKernel(o =>
            {
                o.DataSource = _fixture.DataSource; // externally owned (fixture)
                o.Model(m => m.Document<WorkerNote>().Event<NoteTaken>());
                o.RunFeedWorkers = runFeedWorkers;
                o.Processing.PollInterval = TimeSpan.FromMilliseconds(50);
            })
            .AddChangeHandler<CollectingChangeHandler>()
            .AddEventHandler<CollectingEventHandler>();
        return builder.Build();
    }

    private static string[] FeedWorkers(IHost host) =>
        host.Services.GetServices<IHostedService>()
            .Select(s => s.GetType().Name)
            .Where(n => n.EndsWith("FeedHostedService", StringComparison.Ordinal))
            .ToArray();

    [Fact]
    public void FeedWorkers_AreRegisteredByDefault()
    {
        using var host = BuildHost(runFeedWorkers: true);

        Assert.Equal(["ChangeFeedHostedService", "EventFeedHostedService"], FeedWorkers(host));
    }

    [Fact]
    public async Task WithoutFeedWorkers_NothingIsDelivered_UntilTheTestDrains()
    {
        using var host = BuildHost(runFeedWorkers: false);
        Assert.Empty(FeedWorkers(host));

        await host.StartAsync(); // schema initializer still runs
        try
        {
            var changes = host.Services.GetServices<IChangeHandler>().OfType<CollectingChangeHandler>().Single();
            var events = host.Services.GetServices<IEventHandler>().OfType<CollectingEventHandler>().Single();
            var scope = ScopeContext.Tenant(Guid.NewGuid());
            var noteId = Guid.NewGuid().ToString("N");

            await using (var session = host.Services.GetRequiredService<DocumentStore>().OpenSession(scope))
            {
                await session.SaveAsync(new WorkerNote(noteId, "drained, not polled"), 0);
                await session.AppendAsync(new NoteTaken(noteId));
                await session.CommitAsync();
            }

            await Task.Delay(500); // ten poll intervals: a running worker would have delivered
            Assert.Empty(changes.Received);
            Assert.Empty(events.Received);

            await host.Services.GetRequiredService<ChangeFeedProcessor>().DrainAsync();
            await host.Services.GetRequiredService<EventFeedProcessor>().DrainAsync();

            Assert.Single(changes.Received, c => c.Scope.TenantId == scope.TenantId && c.DocumentId == noteId);
            Assert.Single(events.Received, e => e.Scope.TenantId == scope.TenantId);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
