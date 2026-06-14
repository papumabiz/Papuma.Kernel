// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Hosting;

/// <summary>
/// End-to-end test of the kernel bootstrap (phase 9 DoD): host with
/// <c>AddPapumaKernel</c>, registration scenario + event, delivery to handlers via
/// the hosted workers (NOTIFY-driven).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HostingIntegrationTests
{
    private readonly PostgresFixture _fixture;

    public HostingIntegrationTests(PostgresFixture fixture)
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
            .AddPapumaKernel(o =>
            {
                o.DataSource = _fixture.DataSource; // externally owned (fixture)
                o.Model(m => m
                    .Document<HostUser>()
                    .Document<HostAddress>()
                    .Event<UserRegistered>());
                o.Processing.PollInterval = TimeSpan.FromSeconds(30); // NOTIFY must carry it
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

            // The registration scenario (architecture §5) against the hosted store:
            var store = host.Services.GetRequiredService<DocumentStore>();
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

            // Hosted workers deliver NOTIFY-driven — well below the 30s poll interval.
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

            // Fact and state changes share the session correlation (ADR-013).
            var correlation = (string)changes[0].Metadata["correlationId"]!;
            Assert.Equal(correlation, (string)@event.Metadata["correlationId"]!);
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void AddPapumaKernel_Rejects_MissingOrAmbiguousDatabaseConfig()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddPapumaKernel(o => o.Model(m => m.Document<HostUser>()))); // neither

        Assert.Throws<InvalidOperationException>(() =>
            services.AddPapumaKernel(o =>
            {
                o.ConnectionString = "Host=x";
                o.DataSource = _fixture.DataSource; // both
                o.Model(m => m.Document<HostUser>());
            }));

        Assert.Throws<InvalidOperationException>(() =>
            services.AddPapumaKernel(o => o.ConnectionString = "Host=x")); // no model
    }
}
