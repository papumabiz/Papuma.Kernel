// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;

namespace Papuma.Kernel.Core.Tests.Model;

/// <summary>
/// The logical type name is stored in every row and used to derive key index names, so two registered
/// types must not share it. Before this check two same-named classes in different namespaces built
/// without complaint and silently shared one <c>document_type</c>.
/// </summary>
public sealed class TypeNameCollisionTests
{
    public static class SalesArea
    {
        public sealed record Item(string Id, string Name);

        public sealed record Occurred(string Id);
    }

    public static class StockArea
    {
        public sealed record Item(string Id, int Count);

        public sealed record Occurred(string Id);
    }

    public sealed record Order(string Id);

    public sealed record ORDER(string Id);

    public sealed record Customer(string Id);

    public sealed record Placed(string Id);

    [Fact]
    public void TwoDocumentTypesWithTheSameSimpleName_AreRejected_NamingBothClasses()
    {
        var builder = new KernelModelBuilder().Document<SalesArea.Item>().Document<StockArea.Item>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("SalesArea+Item", ex.Message, StringComparison.Ordinal);
        Assert.Contains("StockArea+Item", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Item'", ex.Message, StringComparison.Ordinal);
        Assert.StartsWith("Document types", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoEventTypesWithTheSameSimpleName_AreRejected()
    {
        var builder = new KernelModelBuilder().Event<SalesArea.Occurred>().Event<StockArea.Occurred>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.StartsWith("Event types", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Occurred'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesThatDifferOnlyInCase_AreRejected_BecauseKeyIndexNamesAreLowerCased()
    {
        var builder = new KernelModelBuilder().Document<Order>().Document<ORDER>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("differ only in case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentAndAnEventMayShareAName_TheyLiveInDifferentTables()
    {
        var model = new KernelModelBuilder().Document<Order>().Event<SalesArea.Occurred>().Build();

        Assert.Single(model.DocumentTypes);
        Assert.Single(model.EventTypes);
    }

    [Fact]
    public void DistinctNames_BuildAsBefore()
    {
        var model = new KernelModelBuilder()
            .Document<Order>()
            .Document<Customer>()
            .Event<Placed>()
            .Build();

        Assert.Equal(["Customer", "Order"], model.DocumentTypes.Select(t => t.Name).Order(StringComparer.Ordinal));
    }
}
