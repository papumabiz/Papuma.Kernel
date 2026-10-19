// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// A dictionary field with free keys must not depend on what the keys look like: before keys were
/// escaped, one key containing a dot made the whole document unsaveable. Here the keys contain dots,
/// through a real PostgreSQL session: the document saves, its history names the key as one segment, and
/// a policy declared on the dictionary still covers it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DottedKeyTests(PostgresFixture fixture) : IAsyncLifetime
{
    private DocumentStore _store = null!;

    private sealed record Project(
        string Id,
        string Name,
        Dictionary<string, string> Settings,
        [property: SensitiveData] Dictionary<string, string> Secrets);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder().Document<Project>(d => d.ExposeToMcp()).Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        _store = new DocumentStore(fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private static Project Make(string id, Dictionary<string, string> settings, Dictionary<string, string>? secrets = null) =>
        new(id, "Alpha", settings, secrets ?? []);

    [Fact]
    public async Task ADocumentWithDottedKeys_Saves_AndItsHistoryNamesEachKeyAsOneSegment()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(Make(id, new() { ["theme"] = "dark", ["feature.beta"] = "on" }), 0);

        await session.SaveAsync(
            Make(id, new() { ["theme"] = "dark", ["feature.beta"] = "off", ["ui.lang"] = "de" }), 1);

        var diff = (await session.GetHistoryAsync<Project>(id))[1].Diff;
        Assert.Equal(["settings.feature\\.beta", "settings.ui\\.lang"], diff.Paths);
        Assert.Equal("on", (string?)diff.Entries["settings.feature\\.beta"].Old);
        Assert.Equal("off", (string?)diff.Entries["settings.feature\\.beta"].New);
        Assert.False(diff.Entries["settings.ui\\.lang"].HasOld);

        var loaded = await session.LoadAsync<Project>(id);
        Assert.Equal("off", loaded!.Document.Settings["feature.beta"]);
        Assert.Equal("de", loaded.Document.Settings["ui.lang"]);
    }

    [Fact]
    public async Task APolicyOnTheDictionary_CoversItsDottedKeys_InTheFeedAndInAMaskedRead()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(Make(id, [], new() { ["api.key"] = "s3cret-1" }), 0);
        await session.SaveAsync(Make(id, [], new() { ["api.key"] = "s3cret-2", ["db.password"] = "hunter2" }), 1);

        var diff = (await session.GetHistoryAsync<Project>(id))[1].Diff;

        Assert.All(diff.Entries.Values, e => Assert.Equal(DiffEntryKind.Redacted, e.Kind));
        Assert.Equal(["secrets.api\\.key", "secrets.db\\.password"], diff.Paths);
        Assert.DoesNotContain("s3cret", diff.ToJson().ToJsonString(), StringComparison.Ordinal);

        var masked = await session.LoadMaskedAsync<Project>(id);
        // The policy sits on the whole field, so the masked read hides the field, keys and all.
        Assert.Equal("[protected]", (string?)masked!.Document["secrets"]);
        Assert.DoesNotContain("s3cret", masked.Document.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", masked.Document.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingADocumentWithDottedKeys_RecordsTheRemovedKeys()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(Make(id, new() { ["feature.beta"] = "on" }), 0);

        await session.DeleteAsync<Project>(id, 1);

        var diff = (await session.GetHistoryAsync<Project>(id))[^1].Diff;
        Assert.True(diff.Entries.ContainsKey("settings"));
        Assert.False(diff.Entries["settings"].HasNew);
    }
}
