// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// Enum values are stored by name, not by number: a number made the feed unreadable, made
/// <c>IsFieldTransition("status", "Pending", "Paid")</c> never match, and turned a reordered or inserted enum member
/// into a silent change of every stored document (ADR-005 calls a new enum value additive). Documents written as
/// numbers before 3.0 still read.
/// </summary>
public sealed class EnumSerializationTests
{
    public enum OrderStatus
    {
        Pending,
        Paid,
    }

    public record Order(string Id, OrderStatus Status, OrderStatus? Previous = null);

    [Fact]
    public void AnEnum_IsWrittenByName()
    {
        var json = JsonSerializer.SerializeToNode(new Order("1", OrderStatus.Paid, OrderStatus.Pending), KernelJson.Options)!;

        Assert.Equal("Paid", (string?)json["status"]);
        Assert.Equal("Pending", (string?)json["previous"]);
    }

    [Fact]
    public void ADocumentWrittenWithNumbers_StillReads()
    {
        var order = JsonSerializer.Deserialize<Order>("""{"id":"1","status":1,"previous":0}""", KernelJson.Options)!;

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(OrderStatus.Pending, order.Previous);
    }

    [Fact]
    public void AStatusTransition_MatchesByName()
    {
        var model = new KernelModelBuilder().Document<Order>().Build();
        var before = JsonSerializer.SerializeToNode(new Order("1", OrderStatus.Pending), KernelJson.Options)!.AsObject();
        var after = JsonSerializer.SerializeToNode(new Order("1", OrderStatus.Paid), KernelJson.Options)!.AsObject();
        var diff = PolicyApplier.Apply(
            JsonDiffEngine.Diff(before, after), model.GetRequired<Order>(), "1", hashKeys: null, ScopeContext.Tenant("tenant1"));
        var change = new ChangeRecord(
            1, ScopeContext.Tenant("tenant1"), "Order", "1", 2, 1, ChangeOperation.Update, diff, "", [], DateTimeOffset.UtcNow);

        Assert.True(change.IsFieldTransition("status", "Pending", "Paid"));
    }
}
