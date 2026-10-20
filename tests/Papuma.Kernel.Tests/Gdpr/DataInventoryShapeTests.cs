// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Serialization;

using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Model;

namespace Papuma.Kernel.Tests.Gdpr;

/// <summary>
/// The Art.-30 inventory is the review tool for policies (ADR-007), so it must see the same paths the metamodel
/// scan protects (review C-1, S-20): the members of structs — and a framework or custom-converted struct stays one leaf.
/// </summary>
public sealed class DataInventoryShapeTests
{
    private record struct Address([property: SensitiveData] string Street, string City);

    private readonly record struct Money(long Cents);

    private sealed record Customer(
        string Id,
        Address Home,
        Address? Work,
        DateTimeOffset CreatedAt,
        Guid Token,
        TimeSpan Age,
        Money Balance);

    [Fact]
    public void Inventory_ListsStructMembers_WithTheirEffectivePolicy_AndKeepsFrameworkStructsAsLeaves()
    {
        var model = new KernelModelBuilder().Document<Customer>().Build();

        var fields = DataInventory.Build(model).Documents.Single().Fields.ToDictionary(f => f.Path, f => f.Policy);

        Assert.Equal(FieldPolicy.Redact, fields["home.street"]);
        Assert.Equal(FieldPolicy.Track, fields["home.city"]);
        Assert.Equal(FieldPolicy.Redact, fields["work.street"]);   // through the Nullable<struct>
        Assert.Equal(FieldPolicy.Track, fields["work.city"]);

        // Framework structs are not nested objects of ours.
        Assert.Contains("createdAt", fields.Keys);
        Assert.Contains("token", fields.Keys);
        Assert.Contains("age", fields.Keys);
        Assert.DoesNotContain(fields.Keys, p => p.StartsWith("createdAt.", StringComparison.Ordinal));
        Assert.DoesNotContain(fields.Keys, p => p.StartsWith("token.", StringComparison.Ordinal));

        // A user-defined struct is walked (its members are the document's), so its own members are listed.
        Assert.Contains("balance.cents", fields.Keys);
    }

    [Fact]
    public void Inventory_AndModel_AgreeOnEveryProtectedPath()
    {
        var model = new KernelModelBuilder().Document<Customer>().Build();
        var protectedByModel = model.GetRequired<Customer>().Policies.Keys.Order(StringComparer.Ordinal).ToList();

        var protectedByInventory = DataInventory.Build(model).Documents.Single().Fields
            .Where(f => f.Policy != FieldPolicy.Track)
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(protectedByModel, protectedByInventory);
    }
}
