// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Hosting;

/// <summary>
/// End-to-end test of the SQLite kernel bootstrap: host with
/// <c>AddPapumaKernelLocal</c>, save documents + append an event, delivery to hosted
/// handlers via the in-process <see cref="Papuma.Kernel.Processing.SqliteChangeNotifier"/>
/// wakeup — proves the signal, feed loop, and DI wiring work together end to end.
/// Mirrors <c>Papuma.Kernel.Tests.Hosting.HostingIntegrationTests</c>.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class HostingIntegrationTests
{
    private readonly SqliteFixture _fixture;

    public HostingIntegrationTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record HostUser(string Id, string Name);

    private sealed record HostAddress(string Id, string City);

    private sealed record UserRegistered(string UserId);

    private sealed class CollectingChangeHandler : IChangeHandler
    {
        public ConcurrentQueue<ChangeRecord> Received { get; } = new();

        public string Name { get; } = $"host_changes_{Guid.NewGuid():N}";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            Received.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private sealed class CollectingEventHandler : IEventHandler
    {
        public ConcurrentQueue<EventRecord> Received { get; } = new();

        public string Name { get; } = $"host_events_{Guid.NewGuid():N}";

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            Received.Enqueue(@event);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Bootstrap_EndToEnd_RegistrationScenarioFlowsToHostedHandlers()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddPapumaKernelLocal(o =>
            {
                o.ConnectionString = _fixture.ConnectionString; // shared temp-file db (fixture)
                o.Model(m => m
                    .Document<HostUser>()
                    .Document<HostAddress>()
                    .Event<UserRegistered>());
                // Deliberately long poll interval — delivery must come from the in-process
                // wakeup signal, not the fallback poll, same intent as the Postgres test.
                o.Processing.PollInterval = TimeSpan.FromSeconds(30);
            })
            .AddChangeHandler<CollectingChangeHandler>()
            .AddEventHandler<CollectingEventHandler>();

        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var changeHandler = host.Services.GetServices<IChangeHandler>()
                .OfType<CollectingChangeHandler>().Single();
            var eventHandler = host.Services.GetServices<IEventHandler>()
                .OfType<CollectingEventHandler>().Single();

            var store = host.Services.GetRequiredService<SqliteDocumentStore>();
            var scope = ScopeContext.Tenant($"t{Guid.NewGuid():N}");
            var userId = Guid.NewGuid().ToString("N");
            var addressId = Guid.NewGuid().ToString("N");

            await using (var session = store.OpenSession(scope))
            {
                await session.SaveAsync(new HostUser(userId, "Harry"), 0);
                await session.SaveAsync(new HostAddress(addressId, "Bonn"), 0);
                await session.AppendAsync(new UserRegistered(userId));
                await session.CommitAsync();
            }

            // Hosted workers deliver signal-driven — well below the 30s poll interval.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline
                   && (changeHandler.Received.Count(c => c.Scope.TenantId == scope.TenantId) < 2
                       || !eventHandler.Received.Any(e => e.Scope.TenantId == scope.TenantId)))
            {
                await Task.Delay(100);
            }

            var changes = changeHandler.Received.Where(c => c.Scope.TenantId == scope.TenantId).ToList();
            Assert.Equal(2, changes.Count);
            Assert.Contains(changes, c => c.DocumentType == nameof(HostUser) && c.DocumentId == userId);
            Assert.Contains(changes, c => c.DocumentType == nameof(HostAddress) && c.DocumentId == addressId);

            var @event = Assert.Single(eventHandler.Received, e => e.Scope.TenantId == scope.TenantId);
            Assert.Equal(userId, @event.Deserialize<UserRegistered>().UserId);

            var correlation = (string)changes[0].Metadata["correlationId"]!;
            Assert.Equal(correlation, (string)@event.Metadata["correlationId"]!);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void AddPapumaKernelLocal_Rejects_MissingOrAmbiguousDatabaseConfig()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddPapumaKernelLocal(o => o.Model(m => m.Document<HostUser>()))); // neither

        Assert.Throws<InvalidOperationException>(() =>
            services.AddPapumaKernelLocal(o =>
            {
                o.DbPath = "x.db";
                o.ConnectionString = "Data Source=y.db"; // both
                o.Model(m => m.Document<HostUser>());
            }));

        Assert.Throws<InvalidOperationException>(() =>
            services.AddPapumaKernelLocal(o => o.DbPath = "x.db")); // no model
    }
}
