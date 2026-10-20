# Papuma Kernel — GDPR Guide

Status: verified against the implemented API (phase 12, 2026-06-12)

Core principle (ADR-015): **the kernel provides executing mechanisms over what
only it knows** (metamodel, history, scopes) — **the application makes the
domain-legal decisions** (which data belongs to the person, erase vs. restrict
vs. retain per tenant).

## First line of defense: PII policy discipline

All personal fields belong under a policy (ADR-007) — then the feed is minimized
out of the box and the sharp tools below are only the safety net:

```csharp
private sealed record User(
    string Id,
    string Name,                                      // deliberately Track? → check the inventory!
    [property: UniqueKey] string Email,
    [property: SensitiveData] string? Iban,           // diff: only "changed"
    [property: TrackHash] string? PasswordHash);      // diff: marker + keyed hash (ADR-025)
```

## Data inventory (Art.-30 support)

A pure metamodel report, no database access — as a record-of-processing
attachment and as a **review tool** ("which fields are unprotected?"), e.g. as a
snapshot test in CI:

```csharp
DataInventoryReport report = DataInventory.Build(model);

foreach (var doc in report.Documents)
{
    Console.WriteLine($"{doc.Name}: unprotected = [{string.Join(", ", doc.UnprotectedPaths)}]");
}

File.WriteAllText("art30-inventory.json", report.ToJson().ToJsonString());
```

The report lists, per document type, all leaf paths with their effective policy
(attribute defaults, fluent overrides and inheritance resolved), keys and schema
version; per event type, the payload paths and the retention. Members of nested objects
(classes and structs) are listed as paths of their own; a collection is one path, because
it is tracked as one value.

**Policies must be able to act where they are declared.** The model refuses what would do
nothing: a policy attribute on a member of a collection's element type (`List<Address>` with
`[SensitiveData]` on `Address.Street`) fails at startup, because a collection is one atomic
value and has no path for "the street of element 2". Declare the policy on the collection
property (`[SensitiveData] List<Address> Homes`, or `.Property(x => x.Homes).Redact()`), or
say `.Track()` if the elements are meant to be stored as they are. More than one policy
attribute on one property is refused as well. If a model once had such a declaration, its
values went into the feed unprotected before: `RedactHistoryAsync` cleans the history up.

## Export (Art. 15 access / Art. 20 portability)

The application supplies the subject→data mapping (via its keys and projections);
the kernel assembles in **one transaction** (consistent snapshot):

```csharp
JsonObject export = await GdprExport.ExportAsync(store, scope,
    documents: [new DocumentRef("User", userId), new DocumentRef("Order", orderId)],
    events: [new EventSelector("UserLoggedIn", "userId", userId)]);
```

Content per document: current state (`exists: false` for deleted ones — the
history still comes), versions, the complete change history with diffs and
metadata. Events are selected generically via payload path = value; overlapping
selectors are deduplicated by the export. **Policy minimization applies
automatically**: diffs and event payloads are stored policy-applied — redacted
values appear in the export only as change markers. The current document state,
by contrast, is the stored truth in plain form (that is the point of an access
request).

## Erasure (Art. 17) — the tenant pattern

The legal situation differs per tenant: tenant A may truly erase, tenant B has
retention obligations (German HGB/AO, 6–10 years). Scope isolation structurally
guarantees that execution in A does not touch tenant B.

**Tenant A — real erasure:**

```csharp
await using var session = store.OpenSession(scopeA,
    new SessionOptions { ActorId = "dpo@company.com" });

await session.DeleteAsync<User>(userId, expectedVersion);          // state gone
await session.RedactHistoryAsync<User>(userId,                     // history cleaned
    reason: "erasure-request-4711");
await session.RedactEventsAsync<UserLoggedIn>(                     // events cleaned
    selectorPath: "userId", selectorValue: userId,
    paths: ["ip", "userAgent"], reason: "erasure-request-4711");
await session.CommitAsync();
```

**Tenant B — restriction instead of erasure (Art. 18):** a blocking status as a
document field, processing restricted application-side, erasure due date noted —
and after the deadline, the same erasure primitive is called on schedule (the
workflow pattern: concepts §18).

```csharp
await session.PatchAsync<User>(userId, p => p
    .Set(x => x.ProcessingRestricted, true)
    .Set(x => x.EraseAfter, new DateOnly(2036, 6, 12)));
```

## `RedactHistoryAsync` / `RedactEventsAsync` — the safety net

Document deletion cleans the feed only for policy-protected fields — a tracked
PII field (e.g. `Name` without an attribute) remains in plain text in historical
diffs after the delete. Exactly this gap is closed by the redaction primitives:

- `RedactHistoryAsync<T>(id, reason, paths?)` rewrites historical diff entries to
  the redaction marker (`paths` covers descendants, and also redacts a whole-object entry
  whose value carries the path — `address.street` reaches the `address` entry of an insert; `null` =
  everything). Also
  works for already-deleted documents. Idempotent.
- `RedactEventsAsync<TEvent>(selectorPath, selectorValue, paths, reason)` removes
  payload fields from selected events (semantics of the Redact event policy: the
  field is absent, consumers see defaults). `selectorValue` is compared **as text with
  the JSON spelling of the field**: `"alice"`, `"42"`, `"true"`, `"1.50"` — so a numeric
  user id is selected with `userId.ToString()`. A JSON `null` or a missing field matches
  nothing. The same on PostgreSQL and SQLite (the engines run one shared case table).

Both are **irreversible** and demand an audit reason; every rewritten row gets a
`redaction` block in its metadata (when, why, actor, correlation). Consequence
per ADR-008: rollback across redacted history fails typed
(`RollbackNotPossibleException`) — the values are gone, intentionally.

This deliberately violates the append-only purity of the feed: **Art. 17 beats
architectural aesthetics.** Redaction is not part of normal application flows —
whoever needs it regularly has a policy omission (→ inventory review).

## Backups revive erased data

Erasure and redaction act on the live database. A backup taken earlier still holds the
data, and a restore brings it back. Keep a log of erasure requests outside the database
and re-apply the ones newer than the backup before serving traffic; keep backup
retention within your erasure deadlines ([backup-restore recipe](recipes/backup-restore.md#3-restore)).

## What deliberately remains application business

| Task | Why not in the kernel |
|---|---|
| Mapping subject → documents/events | domain knowledge (keys, projections) |
| Erase vs. restrict vs. retain per tenant | legal configuration |
| Deadline scheduling ("erase after 10 years") | workflow (concepts §18: `dueAt` + poller) |
| Export delivery (format, encryption, channel) | product decision |
