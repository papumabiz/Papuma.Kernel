// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;
using System.Text.Json.Serialization;

using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Model;

namespace Papuma.Kernel.Tests.Gdpr;

/// <summary>
/// The Art.-30 inventory is the review tool for policies (ADR-007), so it must see the same paths the metamodel
/// scan protects (review C-1, S-20): the members of structs — and a framework or custom-converted struct stays one leaf —
/// and the members of every [JsonDerivedType] subtype (review N-14).
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

    [JsonDerivedType(typeof(Card), "card")]
    [JsonDerivedType(typeof(Transfer), "transfer")]
    private abstract record Payment(decimal Amount);

    private sealed record Card(decimal Amount, [property: SensitiveData] string Pan) : Payment(Amount);

    private sealed record Transfer(decimal Amount, [property: DoNotTrack] string Iban) : Payment(Amount);

    private sealed record Order(string Id, Payment Payment, [property: SensitiveData] object? Payload);

    [Fact]
    public void Inventory_ListsTheMembersOfEveryDerivedType_OnceEach_AndAnObjectAsOneLeaf()
    {
        var model = new KernelModelBuilder().Document<Order>().Build();

        var fields = DataInventory.Build(model).Documents.Single().Fields;
        var byPath = fields.ToDictionary(f => f.Path, f => f.Policy);

        Assert.Equal(FieldPolicy.Redact, byPath["payment.pan"]);
        Assert.Equal(FieldPolicy.DoNotTrack, byPath["payment.iban"]);
        Assert.Equal(FieldPolicy.Track, byPath["payment.amount"]);           // shared by both subtypes, listed once
        Assert.Single(fields, f => f.Path == "payment.amount");
        Assert.Equal(FieldPolicy.Redact, byPath["payload"]);                 // an object is a leaf, not dropped
    }

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

    [Theory]
    [InlineData(typeof(Customer))]
    [InlineData(typeof(Order))]
    public void Inventory_AndModel_AgreeOnEveryProtectedPath(Type documentType)
    {
        var builder = new KernelModelBuilder();
        typeof(KernelModelBuilder).GetMethod(nameof(KernelModelBuilder.Document), 1, [])!.MakeGenericMethod(documentType).Invoke(builder, null);
        var model = builder.Build();
        var protectedByModel = model.DocumentTypes.Single().Policies.Keys.Order(StringComparer.Ordinal).ToList();

        var protectedByInventory = DataInventory.Build(model).Documents.Single().Fields
            .Where(f => f.Policy != FieldPolicy.Track)
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(protectedByModel, protectedByInventory);
    }
}
