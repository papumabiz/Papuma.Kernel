// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Reading a stored document or diff that is nested deeper than the limit (review L-22): the SQLite session
/// reports it as <see cref="DocumentTooDeepException"/> on every read path, like the write paths and the
/// PostgreSQL session do — not as the parser's raw <see cref="JsonException"/> with a byte position. The rows are
/// written straight into the tables: through a session such a document cannot be stored at all, but a file
/// can be edited, restored from a foreign backup or written by a tool.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteStoredDocumentDepthTests(SqliteFixture fixture) : IAsyncLifetime
{
    private SqliteDocumentStore _store = null!;

    public sealed record Tree(string Id, Node? Root);

    public sealed record Node(int V, Node? Child);

    // Schema v2 with an upcaster: a v1 row takes the parse-then-upcast branch of the read path.
    public sealed record Evolved(string Id, string Name);

    public async Task InitializeAsync() =>
        _store = await fixture.CreateStoreAsync(new KernelModelBuilder()
            .Document<Tree>()
            .Document<Evolved>(d => d.Upcast(1, json => json["name"] ??= "upcast"))
            .Build());

    public Task DisposeAsync() => Task.CompletedTask;

    // A document with `levels` levels of nesting in total (the root object is level 1).
    private static string Nested(string id, int levels)
    {
        var inner = levels - 2; // the root and the "root" object are two levels already
        return "{\"id\":\"" + id + "\",\"root\":{"
            + string.Concat(Enumerable.Repeat("\"child\":{", inner))
            + "\"v\":1" + new string('}', inner) + "}}";
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(fixture.ConnectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private Task InsertDocumentAsync(string tenant, string type, string id, string data, long version = 1, int schemaVersion = 1) =>
        ExecuteAsync(
            """
            INSERT INTO document (scope, tenant_id, document_type, id, version, schema_version, data, created_at, updated_at)
            VALUES ('Tenant', @tenant, @type, @id, @version, @schemaVersion, @data, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00')
            """,
            ("tenant", tenant), ("type", type), ("id", id), ("version", version), ("schemaVersion", schemaVersion), ("data", data));

    private Task InsertChangeAsync(string tenant, string id, long version, int operation, string diff) =>
        ExecuteAsync(
            """
            INSERT INTO change (scope, tenant_id, document_type, document_id, version, schema_version, operation, diff, occurred_at)
            VALUES ('Tenant', @tenant, 'Tree', @id, @version, 1, @operation, @diff, '2026-01-01T00:00:00.0000000+00:00')
            """,
            ("tenant", tenant), ("id", id), ("version", version), ("operation", operation), ("diff", diff));

    private static string NewTenant() => $"t{Guid.NewGuid():N}";

    [Fact]
    public async Task LoadAsync_OfAStoredDocumentAtTheLimit_Works()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        await InsertDocumentAsync(tenant, nameof(Tree), id, Nested(id, JsonLimits.MaxDepth));
        await using var session = _store.OpenSession(ScopeContext.Tenant(tenant));

        var loaded = await session.LoadAsync<Tree>(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Document.Id);
    }

    [Fact]
    public async Task LoadAsync_OfAStoredDocumentBeyondTheLimit_ThrowsDocumentTooDeep()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        // The serializer accepts one level more than the parser (KernelJson), so go clearly beyond.
        await InsertDocumentAsync(tenant, nameof(Tree), id, Nested(id, JsonLimits.MaxDepth + 6));
        await using var session = _store.OpenSession(ScopeContext.Tenant(tenant));

        var ex = await Assert.ThrowsAsync<DocumentTooDeepException>(() => session.LoadAsync<Tree>(id));

        Assert.Equal(nameof(Tree), ex.DocumentType);
        Assert.Equal(id, ex.DocumentId);
    }

    [Fact]
    public async Task LoadAsync_OfAStoredDocumentOfAnOlderSchemaBeyondTheLimit_ThrowsDocumentTooDeep()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        var data = $$"""{"id":"{{id}}","deep":{{Deep(JsonLimits.MaxDepth + 5)}}}""";
        await InsertDocumentAsync(tenant, nameof(Evolved), id, data, schemaVersion: 1);
        await using var session = _store.OpenSession(ScopeContext.Tenant(tenant));

        var ex = await Assert.ThrowsAsync<DocumentTooDeepException>(() => session.LoadAsync<Evolved>(id));

        Assert.Equal(nameof(Evolved), ex.DocumentType);
        Assert.Equal(id, ex.DocumentId);
    }

    [Fact]
    public async Task LoadManyAsync_WithAStoredDocumentBeyondTheLimit_ThrowsDocumentTooDeep()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        await InsertDocumentAsync(tenant, nameof(Tree), id, Nested(id, JsonLimits.MaxDepth + 6));
        await using var session = _store.OpenSession(ScopeContext.Tenant(tenant));

        await Assert.ThrowsAsync<DocumentTooDeepException>(() => session.LoadManyAsync<Tree>([id]));
    }

    [Fact]
    public async Task RollbackAsync_OfAStoredDocumentBeyondTheLimit_ThrowsDocumentTooDeep()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        await InsertDocumentAsync(tenant, nameof(Tree), id, Nested(id, JsonLimits.MaxDepth + 1), version: 2);
        await using var session = _store.OpenSession(ScopeContext.Tenant(tenant));

        var ex = await Assert.ThrowsAsync<DocumentTooDeepException>(() => session.RollbackAsync<Tree>(id, 1, 2));

        Assert.Equal(id, ex.DocumentId);
    }

    [Fact]
    public async Task RollbackAsync_OverAStoredDiffBeyondTheLimit_ThrowsDocumentTooDeep()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid().ToString("N");
        await InsertDocumentAsync(tenant, nameof(Tree), id, Nested(id, 3), version: 2);
        await InsertChangeAsync(tenant, id, 1, operation: 1, diff: "{}");
        await InsertChangeAsync(tenant, id, 2, operation: 2, diff: $$"""{"root":{{Deep(JsonLimits.MaxDepth + 5)}}}""");
        await using var session = _store.OpenSession(ScopeContext.Tenant(tenant));

        var ex = await Assert.ThrowsAsync<DocumentTooDeepException>(() => session.RollbackAsync<Tree>(id, 1, 2));

        Assert.Equal(id, ex.DocumentId);
    }

    // `levels` nested objects, as one JSON value.
    private static string Deep(int levels) =>
        string.Concat(Enumerable.Repeat("{\"a\":", levels)) + "1" + new string('}', levels);
}
