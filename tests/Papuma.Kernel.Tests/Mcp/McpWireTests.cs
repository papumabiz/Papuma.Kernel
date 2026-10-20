// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Mcp;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Mcp;

/// <summary>
/// The MCP tools over a real MCP client/server pair (in-memory pipes) — what an agent
/// actually receives: <c>McpException</c> messages reach it, every other exception
/// becomes the SDK's generic text (review M-4), and registration misuse fails loudly (M-7).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class McpWireTests
{
    private readonly PostgresFixture _fixture;

    public McpWireTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record WireDoc(string Id, string Name);

    private sealed class WireChangeHandler : IChangeHandler
    {
        public string Name => "mcp-wire-effect";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class WireEventHandler : IEventHandler
    {
        public string Name => "mcp-wire-event";

        public Task HandleAsync(EventRecord @event, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ActionableErrors_ReachTheAgent_WithTheirMessage()
    {
        await using var harness = await Harness.StartAsync(_fixture.DataSource, _fixture.DataSource);

        var result = await harness.Client.CallToolAsync(
            "get_document_history",
            new Dictionary<string, object?>
            {
                ["documentType"] = nameof(WireDoc),
                ["documentId"] = "x",
                ["tenantId"] = "not a valid tenant!",
            });

        Assert.True(result.IsError);
        Assert.Contains("tenantId is not valid", ToText(result));
    }

    [Fact]
    public async Task InfrastructureErrors_StayGenericOnTheWire()
    {
        // A data source pointing nowhere: the Npgsql exception names host and user — none
        // of it may reach the agent.
        await using var broken = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=wire_user_secret;Password=wire_pw_secret;Timeout=1;Pooling=false");
        await using var harness = await Harness.StartAsync(_fixture.DataSource, broken);

        var result = await harness.Client.CallToolAsync(
            "get_feed_lag");

        Assert.True(result.IsError);
        var text = ToText(result);
        Assert.DoesNotContain("wire_user_secret", text);
        Assert.DoesNotContain("wire_pw_secret", text);
        Assert.DoesNotContain("127.0.0.1", text);
    }

    [Fact]
    public async Task ToolDescriptions_WarnThatReturnedValuesAreUntrusted()
    {
        await using var harness = await Harness.StartAsync(_fixture.DataSource, _fixture.DataSource);

        var tools = await harness.Client.ListToolsAsync();

        foreach (var name in new[]
                 {
                     "get_feed_failures", "get_document_history", "get_changes_by_correlation",
                     "get_document", "get_document_by_key",
                 })
        {
            var description = tools.Single(t => t.Name == name).Description;
            Assert.Contains("untrusted tenant data", description);
        }
    }

    [Fact]
    public void WithPapumaKernel_RejectsANullOptionsTransform()
    {
        var builder = new ServiceCollection().AddMcpServer();

        Assert.Throws<ArgumentNullException>(() => builder.WithPapumaKernel(_ => null!));
    }

    [Fact]
    public void WithPapumaKernel_Twice_IsRejected()
    {
        var builder = new ServiceCollection().AddMcpServer().WithPapumaKernel();

        Assert.Throws<InvalidOperationException>(() => builder.WithPapumaKernel());
    }

    private static string ToText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CancellationTokenSource _stop;
        private readonly Task _serverRun;
        private readonly ChangeFeedProcessor _change;
        private readonly EventFeedProcessor _events;

        private Harness(
            ServiceProvider services, McpClient client, CancellationTokenSource stop, Task serverRun,
            ChangeFeedProcessor change, EventFeedProcessor events)
        {
            _services = services;
            _stop = stop;
            Client = client;
            _serverRun = serverRun;
            _change = change;
            _events = events;
        }

        public McpClient Client { get; }

        /// <summary>Starts the tools over <paramref name="storeSource"/> (the store) and <paramref name="feedSource"/> (the processors).</summary>
        public static async Task<Harness> StartAsync(NpgsqlDataSource storeSource, NpgsqlDataSource feedSource)
        {
            var model = new KernelModelBuilder().Document<WireDoc>().Build();
            await SchemaManager.EnsureSchemaAsync(storeSource, model);
            var change = new ChangeFeedProcessor(feedSource, [new WireChangeHandler()]);
            var events = new EventFeedProcessor(feedSource, [new WireEventHandler()]);

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new DocumentStore(storeSource, model));
            services.AddSingleton(change);
            services.AddSingleton(events);
            services.AddMcpServer()
                .WithStreamServerTransport(
                    clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                .WithPapumaKernel();
            var provider = services.BuildServiceProvider();

            var server = provider.GetRequiredService<McpServer>();
            var stop = new CancellationTokenSource();
            var serverRun = server.RunAsync(stop.Token);

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));

            return new Harness(provider, client, stop, serverRun, change, events);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _stop.Cancel();
            try
            {
                await _serverRun;
            }
            catch (OperationCanceledException)
            {
            }

            await _services.DisposeAsync();
            _change.Dispose();
            _events.Dispose();
            _stop.Dispose();
        }
    }
}
