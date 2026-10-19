// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Dictionary fields whose keys contain dots, through a real SQLite session — the counterpart of
/// <c>DottedKeyTests</c> in the PostgreSQL suite.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteDottedKeyTests(SqliteFixture fixture) : IAsyncLifetime
{
    private SqliteDocumentStore _store = null!;

    private sealed record Project(
        string Id,
        string Name,
        Dictionary<string, string> Settings,
        [property: SensitiveData] Dictionary<string, string> Secrets);

    public async Task InitializeAsync() =>
        _store = await fixture.CreateStoreAsync(new KernelModelBuilder().Document<Project>(d => d.ExposeToMcp()).Build());

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
}
