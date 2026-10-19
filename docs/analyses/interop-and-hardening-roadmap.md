# Roadmap: a storage-neutral spec, hardening, and a Rust sibling

Status: **proposal / not decided** (2026-10-16). Nothing here is scheduled; each
phase has its own trigger and can stop after any step. Written after an
assessment of whether a Rust library should port or re-implement the kernel, and
which weaknesses of the current kernel are worth fixing regardless.

Related: [local-kernel-sqlite-sibling.md](local-kernel-sqlite-sibling.md) (the
Core/Local split this builds on), [offline-sync-and-projection-conflicts.md](offline-sync-and-projection-conflicts.md)
(the sync bridge, still unbuilt), [../feed-wire-format.md](../feed-wire-format.md)
(today's Postgres-only contract).

## Guiding decision

A Rust implementation should be **a new library, not a port**. Compatibility is
achieved through a **shared specification and a shared conformance suite**, not
shared code. The kernel is tied to PostgreSQL (`RETURNING OLD/NEW`, snapshot
cursor, JSONB); a port would carry C# idioms into Rust and still diverge. The
same reasoning that produced `Papuma.Kernel.Local` as an independent sibling
(no shared storage seam) applies a third time.

Consequence: the first work is on the C# side — extracting what is *implicitly*
specified by the code into something a second implementation can be tested
against.

## Findings this roadmap addresses

| # | Finding | Evidence | Phase |
|---|---|---|---|
| F1 | The wire contract is a Postgres contract; there is no storage-neutral spec | `feed-wire-format.md` banner excludes Local; cursor is `pg_snapshot`/`xid8` | 1 |
| F2 | Policy `Hash` is not canonical and not salted | `PolicyHash.Of` hashes `JsonNode.ToJsonString()` (STJ-specific formatting); plain SHA-256 of low-entropy PII is dictionary-reversible | 2 |
| F3 | Core logic is only tested indirectly, through two DB suites | no `Papuma.Kernel.Core.Tests`; analysis §9 defers it | 3 |
| F4 | Patch semantics exist twice | Postgres `jsonb_set` SQL vs. `JsonPatchApplier` in Local | 1, 3 |
| F5 | Diff/key paths are dot-separated strings | `JsonDiffEngine.Diff` already **rejects** object keys containing `.` (`NotSupportedException`, covered by a test), so diffs cannot be corrupted; the remaining limit is that such documents cannot be diffed at all. Not yet checked: whether `RedactionEngine`, `PatchBuilder`, `KeyMetadata` guard the same way | 4 |
| F6 | Number equality in diffs is unspecified | `JsonNode.DeepEquals` in `JsonDiffEngine`; `1` vs `1.0`, `long`/`decimal` edges not pinned by tests | 3 |
| F7 | `document_type` is "the CLR type name" | `feed-wire-format.md` §1 — a C# detail inside the contract | 1 |
| F8 | Ordering guarantees differ per engine | Postgres: causal order, lower `seq` may arrive after higher; Local: monotonic `seq` | 1 |

## Phase 1 — Storage-neutral specification

**Goal:** a document set and test material a second implementation (any
language, any engine) can be verified against.

**Progress (2026-10-16):** first slice landed in [`docs/spec/`](https://github.com/papumabiz/Papuma.Kernel/tree/master/docs/spec) —
diff and policy chapter, JSON Schemas for the diff and the change record, 26 diff
and 13 policy vectors, and a runner (`SpecVectorTests`) against `Papuma.Kernel.Core`.
All vectors passed on the first run, i.e. the spec matches current behaviour.
F6 (2026-10-19): number equality was measured — the engine compares by decimal
value at arbitrary precision (`1` = `1.0` = `1e0`, `0` = `-0`, but `0.1` ≠
`0.10000000000000001`) — and is now pinned by 7 `numbers-*` vectors (26 diff
vectors in total); range limits remain open.
F4 (2026-10-19): the patch catalog is specified in `docs/spec/patch.md` with 30
vectors that run against real PostgreSQL and the SQLite applier. They found six
real divergences, two of them silent update loss in PostgreSQL and two silent data
overwrites in SQLite; all fixed (Phase 1b).
Still open in this phase: canonical JSON, event-log record, ordering chapter (F8), and end-to-end vectors against
each storage engine. `docs/spec` is repository-only for now (not in the
`PackagedDocs` item group).

- `docs/spec/` (English):
  - **Record formats** as JSON Schema: document envelope, `ChangeRecord`,
    `EventRecord`, diff entries (`old`/`new`, `changed`, `hash`, `ref`).
  - **Logical guarantees** instead of Postgres mechanics: per document gapless
    by `version`; causal order across transactions; at-least-once delivery;
    cursor is opaque. `seq` is *an* identity, never a watermark (F8).
  - **Canonical JSON** rules: key order, number normalisation, string escaping
    (needed by Phase 2).
  - **Patch operation catalog** with exact semantics (F4).
  - **Policy semantics**: `Track` / `Redact` / `Reference` / `Hash` / `DoNotTrack`
    including the null-vs-absent rule and atomic arrays.
  - Logical names: `document_type` is a declared name, not a CLR type name (F7).
- `docs/spec/vectors/`: language-neutral test vectors (JSON files): input
  documents / patches / policy sets → expected diffs and `ChangeRecord`
  payloads. **Generated from the existing C# tests**, then reviewed by hand.
- `feed-wire-format.md` becomes "the PostgreSQL realisation of the spec"
  (tables, snapshot cursor), not the contract itself.

**Exit criteria:** the vectors run green against `Papuma.Kernel` *and*
`Papuma.Kernel.Local`. Any difference found here is a real finding (expect some).

**Effort:** small–medium; mostly writing, little code.

## Phase 1b — Close the patch divergences (F4) — done 2026-10-19

All three items landed together with the vectors (see CHANGELOG, ADR-012 clarification):

1. PostgreSQL: `increment` reads from the expression built so far (the two
   lost-update cases).
2. `increment` on a non-numeric value throws `NonNumericIncrementException` on both
   engines instead of overwriting (SQLite) or coercing / leaking a raw
   `PostgresException` (PostgreSQL).
3. Missing / non-object parent for `set` and `increment`: an error on both engines
   (`PatchTargetMissingException`), decided 2026-10-19; PostgreSQL checks it in-process
   against `old.data`.

## Phase 2 — Policy hash: canonical and keyed (F2)

Can run in parallel with Phase 1; its canonical-serialization rule lives in the
spec.

- **ADR-025 (proposed 2026-10-19)** fixes the scheme: HMAC-SHA-256 over canonical JSON, a
  per-tenant key derived by HKDF from an application master key, `v2:<kid>:<hex>` output, a
  key-check value in the database, fail closed without a key, `v1` read-only. Done 2026-10-19: the
  canonical-JSON spec chapter (`docs/spec/canonical-json.md`), the Python reference, 43 canonical
  and 16 hash vectors, and `CanonicalJson` / `HashV2` in Core reproducing them. Wired in as an opt-in the same day:
  `IHashKeyProvider`, `MasterKeyHashKeyProvider`, the options, and the key context in the diff, masked-read
  and event-payload paths of both kernels. Next: the key-check table, fail closed, the test key in
  `Papuma.Kernel.Testing`, then v1 read-only and the upgrade note.
- (Original sketch, superseded by the ADR:) choose the scheme. Likely `HMAC-SHA-256` over canonical JSON with a
  tenant-scoped key from configuration (a salt alone defeats precomputed tables
  but not a keyed attacker model; decide explicitly).
- Versioned output (e.g. `v2:` prefix) so existing `v1` hashes stay readable;
  decide whether `v1` is still *produced* (comparability with stored hashes) or
  only *accepted*.
- Configuration: key provider interface; fail-closed when a `Hash` policy exists
  and no key is configured (or a documented opt-out).
- Tests: stability across runs and platforms, canonical form independent of
  serializer options, `v1` backward read.
- Docs: `gdpr.md` and ADR-007 amendment — be honest about what `Hash` does and
  does not protect against.

**Trigger to prioritise:** any real `Hash` use on low-entropy PII.

## Phase 3 — DB-less Core tests and number semantics (F3, F4, F6) — done 2026-10-19

- `tests/Papuma.Kernel.Core.Tests` exists: Core only, no database, no container. It holds
  the diff engine tests (moved from `Papuma.Kernel.Tests`) and the conformance runners for
  the diff, policy and patch vectors (millisecond feedback; 101 tests in ~150 ms).
- Number behaviour is measured and pinned by the `numbers-*` diff vectors (F6).
- Patch parity (F4): one in-process definition (`JsonPatchApplier`, now in Core); the
  PostgreSQL SQL expression is held to the same vectors on a real PostgreSQL.
- Follow-up, not done: more DB-free candidates still live in `Papuma.Kernel.Tests`
  (e.g. model and policy tests) and can move when they are next touched.

## Phase 4 — Path representation (F5) — audit done 2026-10-19

The diff engine already fails loudly on dotted document keys. The audit found that every
other dotted path comes from one place, `JsonPathResolver.JsonNameOf` (a
`[JsonPropertyName("a.b")]` property), and that such a property could be declared and
only failed on the first save. It now fails at model build / path resolution
(`NotSupportedException`, tested in `Core.Tests`). Dictionary-shaped fields with arbitrary
keys remain unsupported in diffed documents.

Only if such fields occur in a real model:

- **Full (breaking, major):** an escaped path form or an array of segments in the diff.
  Needs a `schema_version`-style marker on the feed, an ADR, and a consumer migration note
  in the wire-format document.

## Phase 5 — Rust sibling (`papuma-kernel`, own repository)

Not started; gated on Phase 1 exit criteria and a real consumer (same rule as
Papuma.Streams: no code without a consumer).

- Shape: pure core (`(old, new | patch, policies) -> (new, ChangeRecord)`, no
  I/O, no database types) + a narrow storage trait; **SQLite first** via
  `rusqlite`, Postgres optional later.
- The core is unit-testable without a database from day one; the Phase 1
  vectors are its conformance suite and run in CI against the C# repository's
  vector files (pinned by version).
- Model: declarative metamodel (type name, keys, policy per path,
  `schema_version`) loadable as data, plus derive macros for the ergonomic path —
  no reflection in Rust.
- Sync readiness (decisions to take *before* data exists, not features): client
  generated IDs (UUIDv7), an installation/device ID in `actor_id`/metadata,
  local `seq` never used as a sync cursor, `IdempotencyKey` on every write.
- Sync bridge to a C# server remains the unbuilt Idea A from
  [offline-sync-and-projection-conflicts.md](offline-sync-and-projection-conflicts.md);
  the Rust side must not bake in assumptions that contradict it.

## Explicitly not planned

- A shared `IDocumentStorage` seam inside C# (rejected in
  [local-kernel-sqlite-sibling.md](local-kernel-sqlite-sibling.md) §6/§7).
- Porting the .NET code (DI, builders, reflection-driven metamodel) to Rust.
- A sync protocol before an offline requirement exists.

## Suggested order

1. **Phase 1** — it unblocks everything else and finds divergence early.
2. **Phase 2** in parallel as soon as the canonical-JSON rule is drafted
   (the ADR can be written against the spec text).
3. **Phase 3** when the vectors exist (they need a runner anyway).
4. **Phase 4** only on a concrete trigger.
5. **Phase 5** when a Rust consumer is real.

## Open questions

- Is `v1` hash output still produced for comparability, or only read? (Phase 2)
- Which side owns the spec vectors long-term — this repository, with Rust
  pinning a released version? (Phase 1/5; proposal: yes.)
- Does the spec live in the NuGet doc set, or repository-only? (Shipping it
  helps coding agents; a separate spec version number may be needed.)
