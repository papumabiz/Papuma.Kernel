// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Gdpr;

/// <summary>
/// Integration tests for the GDPR tooling (ADR-015, phase 12): data inventory,
/// export assembly, and history/event redaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GdprToolingTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;
    private KernelModel _model = null!;

    public GdprToolingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record GdprAddress(string Street, string City);

    private sealed record GdprUser(
        string Id,
        string Name,
        [property: UniqueKey] string Email,
        [property: SensitiveData] string? Iban = null,
        [property: TrackHash] string? PasswordHash = null,
        GdprAddress? Address = null);

    private sealed record GdprLogin(string UserId, string Ip, string UserAgent);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<GdprUser>()
            .Event<GdprLogin>(e => e.Retention(TimeSpan.FromDays(90)))
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
        _model = model;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    // ── Data inventory (Art. 30) ───────────────────────────────────────────────

    [Fact]
    public void Inventory_ListsEffectivePoliciesKeysAndRetention()
    {
        var report = DataInventory.Build(_model);

        var user = Assert.Single(report.Documents, d => d.Name == nameof(GdprUser));
        Assert.Equal(1, user.SchemaVersion);

        var fields = user.Fields.ToDictionary(f => f.Path, f => f.Policy);
        Assert.Equal(FieldPolicy.Track, fields["name"]);
        Assert.Equal(FieldPolicy.Redact, fields["iban"]);
        Assert.Equal(FieldPolicy.Hash, fields["passwordHash"]);
        Assert.Equal(FieldPolicy.Track, fields["address.city"]); // nested leaf enumerated

        Assert.Contains("email", user.UniqueKeys);
        Assert.Contains("name", user.UnprotectedPaths);
        Assert.DoesNotContain("iban", user.UnprotectedPaths);

        var login = Assert.Single(report.Events, e => e.Name == nameof(GdprLogin));
        Assert.Equal(TimeSpan.FromDays(90), login.Retention);
        Assert.Contains("ip", login.UnprotectedPaths);

        var json = report.ToJson();
        Assert.NotNull(json["documents"]);
        Assert.NotNull(json["events"]);
    }

    // ── Export assembly (Art. 15/20) ───────────────────────────────────────────

    [Fact]
    public async Task Export_AssemblesStateHistoryAndSelectedEvents_PolicyMinimized()
    {
        var scope = NewTenant();
        var userId = NewId();
        var otherId = NewId();

        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new GdprUser(userId, "Harry", $"{userId}@x.de", Iban: "DE07123"), 0);
            await session.SaveAsync(new GdprUser(otherId, "Other", $"{otherId}@x.de"), 0);
            await session.AppendAsync(new GdprLogin(userId, "10.0.0.1", "Firefox"));
            await session.AppendAsync(new GdprLogin(otherId, "10.0.0.2", "Chrome"));
            await session.CommitAsync();
        }

        await using (var session = _store.OpenSession(scope))
        {
            var loaded = await session.LoadAsync<GdprUser>(userId);
            await session.SaveAsync(loaded!.Document with { Name = "Harald", Iban = "DE07999" }, loaded.Version);
            await session.CommitAsync();
        }

        var export = await GdprExport.ExportAsync(_store, scope,
            [new DocumentRef(nameof(GdprUser), userId)],
            [new EventSelector(nameof(GdprLogin), "userId", userId)]);

        var doc = Assert.Single(export["documents"]!.AsArray());
        Assert.True((bool)doc!["exists"]!);
        Assert.Equal("Harald", (string?)doc["state"]!["name"]);
        Assert.Equal(2, (long)doc["version"]!);

        var history = doc["history"]!.AsArray();
        Assert.Equal(2, history.Count);
        Assert.Equal("Insert", (string?)history[0]!["operation"]);
        // Policy minimization reaches the export: the IBAN never appears as a value.
        var ibanEntry = history[1]!["diff"]!["iban"]!.AsObject();
        Assert.True((bool)ibanEntry["changed"]!);
        Assert.False(ibanEntry.ContainsKey("old"));
        Assert.False(ibanEntry.ContainsKey("new"));

        // Only the subject's events are selected, not the other user's.
        var evt = Assert.Single(export["events"]!.AsArray());
        Assert.Equal("10.0.0.1", (string?)evt!["payload"]!["ip"]);
    }

    [Fact]
    public async Task Export_DeletedDocument_StillDeliversHistory()
    {
        var scope = NewTenant();
        var userId = NewId();

        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new GdprUser(userId, "Gone", $"{userId}@x.de"), 0);
            await session.DeleteAsync<GdprUser>(userId, 1);
            await session.CommitAsync();
        }

        var export = await GdprExport.ExportAsync(_store, scope,
            [new DocumentRef(nameof(GdprUser), userId)]);

        var doc = Assert.Single(export["documents"]!.AsArray());
        Assert.False((bool)doc!["exists"]!);
        Assert.Equal(2, doc["history"]!.AsArray().Count);
    }

    [Fact]
    public async Task Export_UnknownTypeName_FailsFast()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => GdprExport.ExportAsync(
            _store, NewTenant(), [new DocumentRef("Nope", "x")]));
    }

    // ── RedactHistoryAsync (Art. 17 safety net) ────────────────────────────────

    [Fact]
    public async Task RedactHistory_RewritesTrackedDiffs_WithAuditMetadata()
    {
        var scope = NewTenant();
        var userId = NewId();

        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new GdprUser(userId, "Harry", $"{userId}@x.de"), 0);
            var loaded = await session.LoadAsync<GdprUser>(userId);
            await session.SaveAsync(loaded!.Document with { Name = "Harald" }, 1);
            await session.CommitAsync();
        }

        await using (var session = _store.OpenSession(scope, new SessionOptions { ActorId = "dpo@x.de" }))
        {
            var rewritten = await session.RedactHistoryAsync<GdprUser>(
                userId, reason: "erasure-request-4711", paths: ["name"]);
            Assert.Equal(2, rewritten); // insert diff + update diff both carry 'name'
            await session.CommitAsync();
        }

        await using (var verify = _store.OpenSession(scope))
        {
            var history = await verify.GetHistoryAsync<GdprUser>(userId);
            Assert.All(history, c => Assert.Equal(DiffEntryKind.Redacted, c.Diff.Entries["name"].Kind));
            // Untouched paths keep their values.
            Assert.Equal(DiffEntryKind.Tracked, history[0].Diff.Entries["email"].Kind);

            var audit = history[0].Metadata["redaction"]!.AsObject();
            Assert.Equal("erasure-request-4711", (string?)audit["reason"]);
            Assert.Equal("dpo@x.de", (string?)audit["actorId"]);
            Assert.Equal("name", (string?)audit["paths"]!.AsArray()[0]);
        }
    }

    [Fact]
    public async Task RedactHistory_WithoutPaths_RedactsEverything_AndBlocksRollback()
    {
        var scope = NewTenant();
        var userId = NewId();

        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new GdprUser(userId, "Harry", $"{userId}@x.de"), 0);
            var loaded = await session.LoadAsync<GdprUser>(userId);
            await session.SaveAsync(loaded!.Document with { Name = "Harald" }, 1);
            await session.CommitAsync();
        }

        await using (var session = _store.OpenSession(scope))
        {
            await session.RedactHistoryAsync<GdprUser>(userId, reason: "erasure-request-4712");
            await session.CommitAsync();
        }

        await using (var session = _store.OpenSession(scope))
        {
            var history = await session.GetHistoryAsync<GdprUser>(userId);
            Assert.All(history, c =>
                Assert.All(c.Diff.Entries.Values, e => Assert.Equal(DiffEntryKind.Redacted, e.Kind)));

            // ADR-008 consequence: the values are gone — rollback fails typed.
            await Assert.ThrowsAsync<RollbackNotPossibleException>(
                () => session.RollbackAsync<GdprUser>(userId, toVersion: 1, expectedVersion: 2));
        }
    }

    [Fact]
    public async Task RedactHistory_IsIdempotent_AndRequiresReason()
    {
        var scope = NewTenant();
        var userId = NewId();

        await using var session = _store.OpenSession(scope);
        await session.SaveAsync(new GdprUser(userId, "Harry", $"{userId}@x.de"), 0);

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.RedactHistoryAsync<GdprUser>(userId, reason: "  "));

        Assert.Equal(1, await session.RedactHistoryAsync<GdprUser>(userId, "erasure-1"));
        Assert.Equal(0, await session.RedactHistoryAsync<GdprUser>(userId, "erasure-1"));
    }

    // ── RedactEventsAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RedactEvents_RemovesPayloadFields_OnlyForTheSubject()
    {
        var scope = NewTenant();
        var userId = NewId();
        var otherId = NewId();

        await using (var session = _store.OpenSession(scope))
        {
            await session.AppendAsync(new GdprLogin(userId, "10.0.0.1", "Firefox"));
            await session.AppendAsync(new GdprLogin(otherId, "10.0.0.2", "Chrome"));
            await session.CommitAsync();
        }

        await using (var session = _store.OpenSession(scope))
        {
            var rewritten = await session.RedactEventsAsync<GdprLogin>(
                selectorPath: "userId", selectorValue: userId,
                paths: ["ip", "userAgent"], reason: "erasure-request-4713");
            Assert.Equal(1, rewritten);
            await session.CommitAsync();
        }

        var export = await GdprExport.ExportAsync(_store, scope, [],
            [
                new EventSelector(nameof(GdprLogin), "userId", userId),
                new EventSelector(nameof(GdprLogin), "userId", otherId),
            ]);

        var events = export["events"]!.AsArray();
        Assert.Equal(2, events.Count);

        var subject = Assert.Single(events, e => (string?)e!["payload"]!["userId"] == userId);
        Assert.False(subject!["payload"]!.AsObject().ContainsKey("ip"));
        Assert.Equal("erasure-request-4713", (string?)subject["metadata"]!["redaction"]!["reason"]);

        var other = Assert.Single(events, e => (string?)e!["payload"]!["userId"] == otherId);
        Assert.Equal("10.0.0.2", (string?)other!["payload"]!["ip"]);
    }

    [Fact]
    public async Task Redaction_InTenantA_LeavesTenantBUntouched()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var userId = NewId(); // same id in both tenants — the isolation must come from scope

        foreach (var scope in new[] { tenantA, tenantB })
        {
            await using var session = _store.OpenSession(scope);
            await session.SaveAsync(new GdprUser(userId, "Harry", $"{userId}@x.de"), 0);
            await session.AppendAsync(new GdprLogin(userId, "10.0.0.1", "Firefox"));
            await session.CommitAsync();
        }

        await using (var session = _store.OpenSession(tenantA))
        {
            await session.DeleteAsync<GdprUser>(userId, 1);
            await session.RedactHistoryAsync<GdprUser>(userId, "erasure-request-4714");
            await session.RedactEventsAsync<GdprLogin>("userId", userId, ["ip"], "erasure-request-4714");
            await session.CommitAsync();
        }

        var exportB = await GdprExport.ExportAsync(_store, tenantB,
            [new DocumentRef(nameof(GdprUser), userId)],
            [new EventSelector(nameof(GdprLogin), "userId", userId)]);

        var doc = Assert.Single(exportB["documents"]!.AsArray());
        Assert.True((bool)doc!["exists"]!);
        Assert.Equal("Harry", (string?)doc["state"]!["name"]);
        var insertDiff = doc["history"]!.AsArray()[0]!["diff"]!.AsObject();
        Assert.Equal("Harry", (string?)insertDiff["name"]!["new"]); // tracked, unredacted

        var evt = Assert.Single(exportB["events"]!.AsArray());
        Assert.Equal("10.0.0.1", (string?)evt!["payload"]!["ip"]);
        Assert.Null(evt["metadata"]!["redaction"]);
    }

    [Fact]
    public async Task RedactEvents_RequiresPaths()
    {
        await using var session = _store.OpenSession(NewTenant());
        await Assert.ThrowsAsync<ArgumentException>(() => session.RedactEventsAsync<GdprLogin>(
            "userId", "x", paths: [], reason: "r"));
    }
}
