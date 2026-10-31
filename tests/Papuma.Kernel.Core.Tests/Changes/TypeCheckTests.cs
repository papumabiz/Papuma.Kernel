// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// A handler asks "is this my type?" through the model, not by comparing names: a record carries the logical name
/// (ADR-028), which differs from the class name as soon as a type declares one — and a handler that compared with
/// <c>nameof(Order)</c> then skipped every record without a word.
/// </summary>
public sealed class TypeCheckTests
{
    [DocumentType("order")]
    public record Order(string Id);

    public record Invoice(string Id);

    [EventType("order_paid")]
    public record OrderPaid(string OrderId);

    public record Unregistered(string Id);

    private static readonly KernelModel Model = new KernelModelBuilder()
        .Document<Order>()
        .Document<Invoice>(d => d.Named("invoice_v2"))
        .Event<OrderPaid>()
        .Build();

    private static ChangeRecord Change(string type) =>
        new(1, ScopeContext.Tenant("tenant1"), type, "1", 1, 1, ChangeOperation.Insert, DocumentDiff.Empty, "", [], DateTimeOffset.UtcNow);

    [Fact]
    public void Is_MatchesTheLogicalName_FromAnAttributeOrTheFluentName()
    {
        Assert.True(Change("order").Is<Order>(Model));
        Assert.False(Change(nameof(Order)).Is<Order>(Model));
        Assert.True(Change("invoice_v2").Is<Invoice>(Model));
        Assert.False(Change("order").Is<Invoice>(Model));
    }

    [Fact]
    public void Is_ForAnUnregisteredType_Throws()
    {
        Assert.Throws<DocumentTypeNotRegisteredException>(() => Change("order").Is<Unregistered>(Model));
    }

    [Fact]
    public void AnEventRecord_IsCheckedTheSameWay()
    {
        var paid = new EventRecord(1, ScopeContext.Tenant("tenant1"), "order_paid", [], "", [], DateTimeOffset.UtcNow);

        Assert.True(paid.Is<OrderPaid>(Model));
        Assert.Throws<EventTypeNotRegisteredException>(() => paid.Is<Unregistered>(Model));
    }
}
