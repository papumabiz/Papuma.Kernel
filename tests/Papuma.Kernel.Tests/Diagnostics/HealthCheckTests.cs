// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Papuma.Kernel.AspNetCore.Processing;
using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Diagnostics;

/// <summary>
/// Integration tests for the change-feed lag health check (AspNetCore layer): the
/// threshold behavior that determines Healthy vs. Unhealthy.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HealthCheckTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public HealthCheckTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record HcDoc(string Id, string Name);

    private sealed class IdleHandler : IChangeHandler
    {
        public string Name { get; } = $"hc-{Guid.NewGuid():N}";

        // Never advances: returns without doing work, but the processor only advances
        // the checkpoint when ProcessOnce runs. We deliberately do not run it, so the
        // feed head sits ahead of the checkpoint → lag.
        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<HcDoc>().Build();
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration("papuma", _ => null!, null, null),
    };

    [Fact]
    public async Task Healthy_WhenLagBelowThreshold()
    {
        var handler = new IdleHandler();
        using var processor = new ChangeFeedProcessor(_fixture.DataSource, [handler]);
        await processor.ProcessOnceAsync(); // register + drain: checkpoint == head → lag 0

        var check = new ChangeFeedLagHealthCheck(processor,
            Options.Create(new ChangeFeedHealthCheckOptions { MaxAllowedLag = 1000 }));

        var result = await check.CheckHealthAsync(Context());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Unhealthy_WhenLagExceedsThreshold()
    {
        var handler = new IdleHandler();
        using var processor = new ChangeFeedProcessor(_fixture.DataSource, [handler]);
        await processor.ProcessOnceAsync(); // register the checkpoint at the current head

        // Write after registration but do NOT process → the head moves ahead, lag > 0.
        await using (var session = _store.OpenSession(ScopeContext.Tenant($"t{Guid.NewGuid():N}")))
        {
            await session.SaveAsync(new HcDoc(Guid.NewGuid().ToString("N"), "x"), 0);
            await session.CommitAsync();
        }

        var check = new ChangeFeedLagHealthCheck(processor,
            Options.Create(new ChangeFeedHealthCheckOptions { MaxAllowedLag = 0 }));

        var result = await check.CheckHealthAsync(Context());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(handler.Name, result.Description);
    }
}
