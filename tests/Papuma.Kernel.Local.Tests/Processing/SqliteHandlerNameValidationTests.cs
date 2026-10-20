// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Local.Tests.Processing;

/// <summary>
/// SQLite twin of <c>HandlerNameValidationTests</c> (review P-6, Q-3): a handler's name is its
/// identity in the checkpoint and failure tables, shared by both feeds.
/// </summary>
public sealed class SqliteHandlerNameValidationTests
{
    private const string ConnectionString = "Data Source=unused.db";

    public static TheoryData<string?> InvalidNames => new()
    {
        null,
        "",
        "  ",
        "event:orders",
        "a:b",
        "orders\n",
        new string('a', 101),
    };

    private sealed class NamedChangeHandler(string? name) : IChangeHandler
    {
        public string Name => name!;

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NamedEventHandler(string? name) : IEventHandler
    {
        public string Name => name!;

        public Task HandleAsync(EventRecord @event, CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void SqliteChangeFeedProcessor_RejectsAnInvalidHandlerName(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new SqliteChangeFeedProcessor(ConnectionString, [new NamedChangeHandler(name)]));

        Assert.Equal("handlers", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void SqliteEventFeedProcessor_RejectsAnInvalidHandlerName(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new SqliteEventFeedProcessor(ConnectionString, [new NamedEventHandler(name)]));

        Assert.Equal("handlers", ex.ParamName);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("order-summary")]
    [InlineData("Order.Summary_v2")]
    public void BothProcessors_StillAcceptValidNames(string name)
    {
        using var change = new SqliteChangeFeedProcessor(ConnectionString, [new NamedChangeHandler(name)]);
        using var events = new SqliteEventFeedProcessor(ConnectionString, [new NamedEventHandler(name)]);

        Assert.True(change.HasHandlers && events.HasHandlers);
    }
}
