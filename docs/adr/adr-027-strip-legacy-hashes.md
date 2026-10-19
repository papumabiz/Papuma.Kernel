# ADR-027 — Removing legacy v1 hashes from history: an explicit, resumable sweep

## Status

Proposed (2026-10-19) — extends [ADR-015](adr-015-gdpr-tooling.md) (history redaction) for one
specific, known-weak kind of data left behind by [ADR-025](adr-025-keyed-hash-policy.md). Both
kernels, the change feed and the event log. Not implemented.

## Context

ADR-025 made the `Hash` policy keyed and stopped the kernel from writing the old hash. It
deliberately left the history alone: records written earlier keep their **v1** value — the
unsalted SHA-256 of the serializer's JSON text. That value is exactly what ADR-025 set out to get
rid of: for a low-entropy field (a phone number, a date of birth, a short customer number) it can
be recovered by dictionary attack, so it is personal data in effect, sitting in an append-only
feed that other processes read. It also has almost no remaining value: a v1 hash never matches a
v2 hash of the same value, so what it still proves is only "this field changed once".

Today the cleanup is possible but awkward. `RedactHistoryAsync<T>(id, reason, paths)` already
rewrites a hashed entry to `{ "changed": true }` — but per document id, and the kernel offers no
"all documents of a type" (ADR-026 keeps it out on purpose), so an application would have to
enumerate every id itself. `RedactHistoryAsync` with `paths: null` would also destroy the
legitimately tracked values of the same documents. For events, `RedactEventsAsync` needs a
selector (`userId = X`), not "every event of this type".

The need is a **one-time, deliberate sweep** after the upgrade, not a routine operation.

## Decision

### 1. Two explicit operations, not an automatic migration

```csharp
// documents: v1 hashes in change diffs of one type, in the session's scope
Task<StripResult> StripLegacyHashesAsync<T>(string reason, long afterSeq = 0, int limit = 1000, CancellationToken ct = default);

// events: v1 hashes in payload fields of one event type, at the named paths
Task<StripResult> StripLegacyEventHashesAsync<TEvent>(IReadOnlyCollection<string> paths, string reason,
                                                      long afterSeq = 0, int limit = 1000, CancellationToken ct = default);

public readonly record struct StripResult(int Rewritten, long NextAfterSeq, bool Exhausted);
```

On `DocumentSession` and `SqliteDocumentSession`, same shape. **Nothing runs on upgrade and nothing
runs by itself**: rewriting history is irreversible (ADR-015), so it is an act someone decides on,
with a reason, an actor and a backup in hand. There is no hosted worker and no option that turns it
on.

### 2. What is rewritten

- **Documents.** A change diff entry of kind `Hashed` whose `hash` is a bare 64-digit lowercase hex
  string — a v1 value; a v2 value starts with `v2:` — becomes `{ "changed": true }`. Nothing else is
  touched: v2 hashes, tracked, redacted and reference entries stay. The wire form identifies v1
  unambiguously, so no model lookup is needed.
- **Events.** Payload fields hold the hash as a bare string in the payload's natural shape, so v1
  cannot be told from any other 64-digit hex string by form alone. The caller therefore **names the
  paths**, each path must carry a `Hash` policy in the current model (otherwise the call throws), and
  only a value that is a bare 64-hex string at that path is removed. The field becomes absent — the
  semantics of the `Redact` event policy. A path that was `Track` before it became `Hash` and holds a
  64-hex string for some other reason is the residual risk; naming the paths is the caller's
  attestation that this does not happen, and the audit block records the paths.

### 3. Scope, order and bounded work

Both operations act in the **session's scope** (RLS on PostgreSQL), like every other write: a sweep
over all tenants is the application looping over its tenants — it knows them, the kernel does not
enumerate them. Rows are visited in ascending `seq` after `afterSeq`; at most `limit` rewritten
rows (1 to 1000) per call. The result carries `NextAfterSeq`, the position to continue from, and
`Exhausted` when nothing is left behind it. A caller loops, committing between calls, so no
transaction holds the history of a whole type: the same paging shape as `LoadAllByKeyAsync`
(ADR-026). The operation is **idempotent and resumable**: a rewritten row no longer matches, and a
crash between calls loses nothing but the position.

### 4. Audit

Every rewritten record gets the `redaction` audit block of ADR-015 (when, why, actor, correlation)
with an `operation` of `strip-legacy-hash`; the event variant adds the paths. The reason is
mandatory. As with `RedactHistoryAsync`, the call runs in the session transaction and needs a commit.

### 5. What it does not do

- **It cannot re-hash.** The value behind a v1 hash is gone, so the entry is reduced to "changed",
  never converted to v2.
- **It does not reach copies.** Consumers that already took a v1 hash into a search index, a bus or a
  projection keep it. The rewritten feed row is not redelivered. A projection is repaired by bumping
  its `IProjection.Version` (ADR-024), which replays the now-clean feed; the application cleans the
  rest.
- **It does not reach backups.** An older backup still holds the v1 hashes and a restore brings them
  back ([backup-restore](../recipes/backup-restore.md#3-restore)); re-run the sweep after a restore
  from before it.
- **It changes no rollback behaviour.** A hashed entry never carried values, so ADR-008's
  "rollback across redacted history fails typed" already held for it.

## Consequences

- **Positive:** An application can remove the weak hashes of a whole type in a few loops, with an
  audit trail, without enumerating ids or touching the tracked values next to them.
- **Positive:** The ADR-025 promise — no dictionary-reversible value in the feed — becomes true for
  history as well, on the application's schedule.
- **Positive:** Bounded, resumable calls: no long locks on `papuma.change`, safe to run during
  traffic, safe to interrupt.
- **Negative:** Another irreversible tool next to `RedactHistoryAsync`, and for events a judgement
  the caller must make (the paths).
- **Negative:** A sweep reads `papuma.change` rows of the type by `seq`; on a large table that is a
  scan. The `limit` bounds the writes, not the reads, so it is a maintenance-window tool for big
  histories.
- **Negative:** It leaves downstream copies and backups as they are (§5); the guide has to say so.
- **Neutral:** Applications that never had a `[TrackHash]` field, or that do not care about history,
  never call it.

## Alternatives considered

- **Strip automatically on upgrade or at startup.** Irreversible history rewriting without a decision,
  an actor or a reason contradicts ADR-015, and a failed migration at startup is the worst moment to
  find out. Rejected.
- **Loop `RedactHistoryAsync` over ids.** Possible today, but needs the id list the kernel refuses to
  provide, and its path form rewrites tracked entries of the same documents if used carelessly.
- **A SQL recipe against `papuma.change`.** Violates the rule that nothing writes to `papuma.*`
  directly, skips the audit block, and is different on two engines.
- **Re-hash current values under v2.** Only the latest value is known, not the one each historical
  hash covered. A partial fix that invites confusion about which entries are keyed.
- **Convert to a salted placeholder instead of removing `hash`.** Adds a third form of hash for no
  consumer; `{ "changed": true }` already says everything the v1 value still can.
- **A tenant-wide `All` scope sweep.** Convenient, but erasure-style operations stay scope-bound so
  that tenant isolation (ADR-015) holds structurally; the application loops over tenants.

## Related

- [ADR-025](adr-025-keyed-hash-policy.md) — the keyed hash that left v1 behind, and the read-only v1
  rule this sweep finishes.
- [ADR-015](adr-015-gdpr-tooling.md) and [gdpr.md](../gdpr.md) — the redaction primitives and the
  audit block this reuses.
- [ADR-008](adr-008-rollback-is-update.md) — rollback and redacted history.
- [ADR-024](adr-024-projections-and-effect-handlers.md) — rebuilding projections that took v1 hashes.
- [ADR-026](adr-026-load-all-by-key.md) — the paging shape; and why there is no "all documents of a
  type" to loop over.
