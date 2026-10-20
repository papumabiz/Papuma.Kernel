// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// The key index DDL interpolates path segments, the document type and the index name into SQL text, so each is
/// validated where it is used (review S-12, L-18). The segment validator used to be the document-type validator,
/// which rejected a one-letter property (<c>X</c>) and a leading underscore (<c>_id</c>) with "Invalid documentType".
/// </summary>
public sealed class SqliteKeyIndexDdlSegmentTests
{
    private sealed record ShortName(string Id, [property: UniqueKey] string X);

    private sealed record Underscore(string Id, [property: UniqueKey, JsonPropertyName("_id")] string External);

    private sealed record Dashed(string Id, [property: UniqueKey, JsonPropertyName("a-b")] string Value);

    [Fact]
    public void AOneLetterKeyProperty_GetsItsIndex()
    {
        var ddl = SqliteSchemaManager.BuildKeyIndexDdl(new KernelModelBuilder().Document<ShortName>().Build());

        Assert.Contains("json_extract(data, '$.x')", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeadingUnderscoreKeyProperty_GetsItsIndex()
    {
        var ddl = SqliteSchemaManager.BuildKeyIndexDdl(new KernelModelBuilder().Document<Underscore>().Build());

        Assert.Contains("json_extract(data, '$._id')", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyPropertyWhoseJsonNameCouldNotBeQuotedSafely_IsRefused_BlamingTheSegment()
    {
        var model = new KernelModelBuilder().Document<Dashed>().Build();

        var ex = Assert.Throws<ArgumentException>(() => SqliteSchemaManager.BuildKeyIndexDdl(model));

        Assert.Equal("segment", ex.ParamName);
        Assert.Contains("a-b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentTypeNameThatIsNotAnIdentifier_IsRefusedWhereItIsInterpolated()
    {
        var model = ModelWith("Order'; DROP TABLE papuma.document; --", indexName: "ux_papuma_doc_order_x");

        var ex = Assert.Throws<ArgumentException>(() => SqliteSchemaManager.BuildKeyIndexDdl(model));

        Assert.Equal("documentType", ex.ParamName);
    }

    [Fact]
    public void AnIndexNameThatIsNotAnIdentifier_IsRefusedWhereItIsInterpolated()
    {
        var model = ModelWith("Order", indexName: "ux_papuma_doc_order_x ON papuma.document (id); DROP TABLE x; --");

        var ex = Assert.Throws<ArgumentException>(() => SqliteSchemaManager.BuildKeyIndexDdl(model));

        Assert.Equal("indexName", ex.ParamName);
    }

    // A model assembled by hand, past the builder's checks: what a bug elsewhere could hand to the DDL.
    private static KernelModel ModelWith(string documentType, string indexName)
    {
        var key = new KeyMetadata("x", Unique: true, indexName);
        var metadata = new DocumentTypeMetadata(
            documentType, typeof(ShortName), new Dictionary<string, FieldPolicy>(), [key],
            _ => "id", [], validator: null, exposedToMcp: false);
        return new KernelModel(
            new Dictionary<Type, DocumentTypeMetadata> { [typeof(ShortName)] = metadata },
            new Dictionary<Type, EventTypeMetadata>());
    }
}


/// <summary>
/// A key on a one-letter property, end to end: the index is created (the UNIQUE constraint is enforced) and the
/// lookup finds the document. Before the fix the schema step refused the property with "Invalid documentType".
/// </summary>
[Collection(Papuma.Kernel.Local.Tests.Infrastructure.SqliteCollection.Name)]
public sealed class ShortKeyEndToEndTests(Papuma.Kernel.Local.Tests.Infrastructure.SqliteFixture fixture)
{
    private sealed record ShortKeyed(string Id, [property: UniqueKey] string X);

    [Fact]
    public async Task AUniqueKeyOnAOneLetterProperty_IsEnforced_AndLooksUp()
    {
        var store = await fixture.CreateStoreAsync(new KernelModelBuilder().Document<ShortKeyed>().Build());
        await using var session = store.OpenSession(Papuma.Kernel.Tenancy.ScopeContext.Tenant($"t{Guid.NewGuid():N}"));
        var id = Guid.NewGuid().ToString("N");
        await session.SaveAsync(new ShortKeyed(id, "v"), 0);

        Assert.Equal(id, (await session.LoadByKeyAsync<ShortKeyed>(x => x.X, "v"))!.Document.Id);
        await Assert.ThrowsAsync<UniqueKeyViolationException>(
            () => session.SaveAsync(new ShortKeyed(Guid.NewGuid().ToString("N"), "v"), 0));
    }
}
