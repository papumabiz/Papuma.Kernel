// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Core.Tests.Processing;

/// <summary>The feed handler name rules (review P-6, Q-3).</summary>
public sealed class FeedHandlerNamesTests
{
    public static TheoryData<string?> InvalidNames => new()
    {
        null,
        "",
        " ",
        "\t",
        "event:orders",   // would share the checkpoint of the event handler "orders"
        "a:b",
        ":",
        "orders:",
        "has space",
        "-leading-dash",
        ".leading-dot",
        "_leading-underscore",
        "tab\there",
        "line\nbreak",
        "orders\n",       // '$' would accept a trailing newline; the rule must not
        "bestellung-ä",
        "slash/name",
        new string('a', FeedHandlerNames.MaxLength + 1),
    };

    public static TheoryData<string> ValidNames => new()
    {
        "a",
        "A",
        "7",
        "orders",
        "order-summary",
        "order_summary",
        "Order.Summary.v2",
        "on-order-placed",
        "nats-change-publisher",
        "todo_change_logger",
        "lifecycle-0123456789abcdef0123456789abcdef",
        new string('a', FeedHandlerNames.MaxLength),
    };

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Validate_RejectsInvalidNames_BlamingTheGivenParameter(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(() => FeedHandlerNames.Validate(name, typeof(FeedHandlerNamesTests), "handlers"));

        Assert.Equal("handlers", ex.ParamName);
        Assert.Contains(nameof(FeedHandlerNamesTests), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ValidNames))]
    public void Validate_AcceptsValidNames(string name) =>
        FeedHandlerNames.Validate(name, typeof(FeedHandlerNamesTests), "handlers");

    [Fact]
    public void Validate_ReservedColon_IsExplainedInTheMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => FeedHandlerNames.Validate("event:orders", typeof(FeedHandlerNamesTests), "handlers"));

        Assert.Contains("event:<name>", ex.Message, StringComparison.Ordinal);
    }
}
