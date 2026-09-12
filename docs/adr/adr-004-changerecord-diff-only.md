# ADR-004: ChangeRecord stores a reversible diff, no snapshots

## Status

Accepted (2026-06-11)

## Context

The original draft (chat-1.md) had `Before`, `After` **and** `Diff` in the
ChangeRecord. That triples the storage per change — with large aggregates and a
high change frequency, the feed quickly becomes heavier than the documents
themselves. At the same time, before/after are reconstructible from the diff if
the diff carries both value sides.

Candidates for the diff format:

- **RFC 6902 (JSON Patch)**: standardized, but only forward-applicable (no `old`),
  and unwieldy for projections ("op/path/value" lists instead of a field view).
- **Structured field diff** with `old`/`new` per path: reversible, directly
  projection-friendly (`WhenFieldChanged`), policy-capable per field.

## Decision

1. The ChangeRecord contains **only the diff**, no before/after snapshots.
2. Format: **reversible field diff** — a JSONB map from JSON path to entry:

   ```json
   {
     "email":          { "old": null,    "new": "harry@example.com" },
     "address.city":   { "old": "Köln",  "new": "Bonn" },
     "roles":          { "old": ["user"], "new": ["user", "admin"] }
   }
   ```

   On `Insert` every `old` side is absent, on `Delete` every `new` side (the
   delete diff thus contains the last state — subject to policies, ADR-007).

   **Ruled in phase 2 (2026-06-11):**
   - **Null ≠ absent**: whether a field existed is encoded via the *presence* of
     the `old`/`new` keys (key omitted = field did not exist); the *value* may
     legitimately be JSON `null`. "Field added" and "field changed from null" are
     thus distinguishable — a prerequisite for reversibility.
   - **Arrays are atomic leaf values**: if arrays differ, exactly one entry on the
     array path is produced with the full old and new array. No index diffing
     (`roles[2]`) — that avoids the ambiguity of shifted indices and keeps
     Apply/Reverse trivially correct. Element granularity is a later optimization.
   - **Nested objects** are diffed recursively (`address.city`); paths are
     dot-separated. Keys that themselves contain '.' are not supported
     (unreachable with serialized POCOs).
3. **Historical states** are produced by applying diffs backwards from the current
   document (or forwards from the insert). That is an audit/replay tool, not a hot
   path.
4. Optional **snapshots** (e.g. every n versions) are a later optimization should
   reconstruction become too expensive — not part of the initial scope.

## Rejected alternative: third-party diff libraries

Evaluated (2026-06-11): `SystemTextJson.JsonDiffPatch` (jsondiffpatch delta format
on `JsonNode`, reversible, LCS array diffing), `JsonPatch.Net`/json-everything
(RFC 6902) and `JsonDiffPatch.Net` (Newtonsoft). Decision: **own engine**, because
the wire format is the product here, not the tool:

- **RFC 6902 is not reversible** (`replace` carries no old value) — disqualified
  for ADR-004.
- **jsondiffpatch deltas** are reversible but nested and encoded with magic
  markers — the flat dot paths would be lost, on which policy application
  (ADR-007, exactly one entry per field), `WhenFieldChanged` filters and SQL
  queryability of the diff (`diff ? 'email'`) all rest. Its LCS array diffing also
  solves exactly the complexity we deliberately excluded with "arrays atomic".
- The own engine is ~150 lines with property-tested roundtrip invariants and no
  package dependency (AGENTS.md: prefer the BCL).

Should element granularity for arrays become necessary later,
`SystemTextJson.JsonDiffPatch` is the first candidate — then as an internal
algorithm behind the existing wire format, not as a format change.

## Consequences

- The feed stays lean; storage grows with the size of the change, not of the
  aggregate.
- Rollback (ADR-008) is trivial: invert the diff, apply as an update.
- Policies act per field on exactly one place (the diff entry), not on three.
- Whoever needs the full state at a point in time pays replay costs — accepted,
  since the common case (current state) is always a direct document load.
- **Measured (2026-06-12, closes plan risk #4):** the diff scales linearly with
  the leaf count — ~2 µs at 10 leaves, ~16 µs at 100, ~0.3 ms at 1,000, ~4 ms
  at 10,000 (details: `benchmarks/Papuma.Kernel.Benchmarks/README.md`).
  Recommendation: up to ~1,000 leaf fields per document the diff is negligible
  (cheaper than the accompanying Postgres roundtrip); beyond ~10,000 leaves the
  cost is a symptom of an aggregate bundling independent lifecycles — remodel
  rather than optimize.
