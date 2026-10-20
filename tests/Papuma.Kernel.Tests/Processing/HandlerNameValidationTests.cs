// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Tests.Processing;

/// <summary>
/// A handler's name is its identity in the checkpoint and failure tables, shared by both feeds
/// (review P-6, Q-3): the processors reject names that are missing, too long or contain
/// <c>:</c> — a change handler called <c>event:orders</c> used to share a cursor with the event
/// handler <c>orders</c>. Needs no database: the constructors only validate.
/// </summary>
public sealed class HandlerNameValidationTests
{
    private static readonly NpgsqlDataSource DataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");

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
    public void ChangeFeedProcessor_RejectsAnInvalidHandlerName(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new ChangeFeedProcessor(DataSource, [new NamedChangeHandler(name)]));

        Assert.Equal("handlers", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void EventFeedProcessor_RejectsAnInvalidHandlerName(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new EventFeedProcessor(DataSource, [new NamedEventHandler(name)]));

        Assert.Equal("handlers", ex.ParamName);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("order-summary")]
    [InlineData("Order.Summary_v2")]
    public void BothProcessors_StillAcceptValidNames(string name)
    {
        using var change = new ChangeFeedProcessor(DataSource, [new NamedChangeHandler(name)]);
        using var events = new EventFeedProcessor(DataSource, [new NamedEventHandler(name)]);

        Assert.True(change.HasHandlers);
        Assert.True(events.HasHandlers);
    }

    [Fact]
    public void TheSameNameInBothFeeds_IsAllowed_BecauseTheEventFeedPrefixesItsCheckpoints()
    {
        using var change = new ChangeFeedProcessor(DataSource, [new NamedChangeHandler("orders")]);
        using var events = new EventFeedProcessor(DataSource, [new NamedEventHandler("orders")]);

        Assert.True(change.HasHandlers && events.HasHandlers);
    }

    [Fact]
    public void DuplicateNames_StillRejected_AfterTheNameRules()
    {
        Assert.Throws<ArgumentException>(() => new ChangeFeedProcessor(
            DataSource, [new NamedChangeHandler("orders"), new NamedChangeHandler("orders")]));
    }
}
