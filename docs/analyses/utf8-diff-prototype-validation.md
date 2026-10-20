# Analysis: validating the UTF-8 diff prototype

Status: **evaluation, nothing wired into the sessions** (2026-10-19). The prototype is
`Utf8JsonDiffEngine` / `Utf8Diff` in `src/Papuma.Kernel.Core/Changes` (internal). It computes the
diff of `JsonDiffEngine` on UTF-8 text through `JsonDocument`, without a node tree, skipping
byte-identical subtrees. This document records what was verified, what is not covered yet, the
semantic decisions it surfaced, and what the benchmarks do and do not show.

## 1. Confirmed correctness

| Check | Where | Result |
|---|---|---|
| Differential against `JsonDiffEngine` (oracle), every case varying the change (few / many / unrelated / identical / insert / delete / deep), the spelling of each side (compact with escaped non-ASCII, relaxed, indented, equivalent = shuffled keys + respelled numbers) and the input buffer (byte array, slice of a larger array, UTF-16 string, pooled buffer overwritten and returned before the result is used) | `Utf8JsonDiffEngineDifferentialTests` | 4,000 cases in CI, **100,000 locally (`PAPUMA_DIFF_CASES=100000`): 0 differences** |
| Compared per case: same paths in the same order, same `HasOld`/`HasNew`, equal values; the same for the diff read back from the prototype's wire form; `Apply` and `ApplyReverse` rebuild both states | same | all hold |
| 30 hand-picked agreeing cases (null vs absent, empty containers, all type changes, array insert/delete/move/element change, number equality incl. `9007199254740993`/`…992` and `0.1`/`0.10000000000000001`, escapes in keys and values, key order, whitespace, `.` `\` emoji control-char quote keys) | `Utf8JsonDiffEngineContractTests` | all agree |
| Real PostgreSQL 18 `jsonb`: states from `UPDATE … RETURNING old.data, new.data` as `::text`, as UTF-8 (`convert_to`) and as the provider stream; feed stored as `jsonb` and read back | `Utf8DiffJsonbTests` (Testcontainers) | 300 cases in CI: 0 differences; reconstruction holds |
| Ownership: the result survives its input buffers being overwritten, does not keep them reachable (weak-reference test), is independent of the parsed documents, and holds only the changed values | contract tests | all hold |

## 2. What PostgreSQL `jsonb` guarantees — and what the library promises in general

Pinned by `Utf8DiffJsonbTests.Jsonb_NormalizesTheTextLikeThis` (PostgreSQL 18):

- Keys sorted by **UTF-8 byte length, then bytes**; duplicate keys collapsed, **the last one wins**.
- Separators `", "` and `": "`; strings with minimal escaping and literal non-ASCII.
- Numbers: the **exponent is resolved, the scale is kept** — `1e2` → `100`, `1E-2` → `0.01`, but
  `1.0` stays `1.0`, `0.10` stays `0.10`; `-0` → `0`, `-0.0` → `0.0`. (This corrects the
  expectation in spec changes.md §6, "normalises exponent forms": true, but not the scale.)
- The same JSON value therefore produces the same text **unless numbers differ in scale** — the
  random test found such cases, and the diff correctly reports none of them as changes.
- The provider stream (`GetStream` on a `jsonb` column) is the **binary jsonb format: a version
  byte `0x01`, then the UTF-8 text**. A session reading bytes instead of `::text` must skip it.

What the library promises for **any** JSON input (not only `jsonb`): the diff is a function of the
JSON values. Byte-identical subtrees are skipped because identical bytes are always equal JSON;
everything else is compared semantically. Canonical input only makes the prototype faster. The
`jsonb` facts above are performance assumptions, never correctness assumptions — the
adversarial benchmarks and the indented/shuffled/escaped test spellings exercise exactly the
inputs where they do not hold.

## 3. Findings outside the prototype

### 3.1 A nested privacy policy does not reach whole-value entries (security, both engines)

A policy declared below a diff entry's path does not apply to that entry. The policy resolution
walks from the entry path *up* to ancestors, never down into the value the entry carries.
Reproduced with `Address.Street` marked `[SensitiveData]` (policy `address.street = Redact`):

| Write | Stored diff |
|---|---|
| insert | `{"address":{"new":{"street":"SECRET-STREET","city":"Bonn"}}, …}` |
| delete | `{"address":{"old":{"street":"SECRET-STREET","city":"Bonn"}}, …}` |
| `address`: `null` → object | `{"address":{"old":null,"new":{"street":"SECRET-STREET","city":"Bonn"}}}` |
| only `street` changes | `{"address.street":{"changed":true}}` (correct) |

Every entry that carries an object value is affected: insert, delete, a field that is added or
removed with an object value, any type change to or from an object. The property test
`Policies_NeverLetAProtectedValueThrough_AndKeepTheRest` misses it because it declares policies
only on diff paths and their ancestors, never on descendants. This violates spec changes.md §3
("No code path may store an unpoliced diff").

**Fixed** (2026-10-20, `PolicyApplier.SplitAtPolicies`, so it covers both engines and the prototype):
an entry whose value holds a subtree with a different effective policy is split before the policies
apply — the container keeps the rest of its value, each such subtree gets an entry of its own (added
or removed, from the side that held it), recursively. `Apply` no longer creates a missing parent when
removing, so the split diff stays reversible (a container sorts before its descendants). Spec
changes.md §3 *Policies below an entry*, 8 new policy vectors, unit tests (`NestedPolicySplitTests`),
two property tests in `EngineRobustnessTests` (no marker of a protected path in any stored diff, with
policies on descendants; the split diff rebuilds both states) and session tests on both engines
(`NestedPolicyFeedTests`, `SqliteNestedPolicyFeedTests`). With the split disabled, the property test
fails at its first seed.

### 3.3 GDPR erasure had the same blind spot (fixed)

`RedactionEngine.RedactDiffEntries` erased only the entries whose path is covered by an erasure path — an
erasure of `address.street` did not touch an entry `address` that carries the street inside its value.
Diffs written before the fix in §3.1 contain such entries (and fields without a policy still produce them),
so erasing a nested field left its old values in history. **Fixed** (2026-10-20): an ancestor entry is
redacted as a whole when its old or new value holds the erased path; a split container that no longer holds
it keeps its values. Tests: `RedactionEngineTests`, two `GdprToolingTests` cases on PostgreSQL (one with a
stored diff rewritten into the pre-fix shape), `SqliteNestedErasureTests`.

### 3.2 Depth limits disagree

`JsonLimits.MaxDepth` is 128, but the sessions parse with `JsonNode.Parse`'s default of **64**,
and `JsonSerializer` serializes documents with its default of 64 as well. Between 65 and 128
levels a document is accepted by the engine API but rejected on every session read path
(pinned by `TheSessionsParseLimit_IsLowerThanTheEngineLimit`). A patch can store a document
deeper than 64 (a 64-level value set below a non-root path), whose next update then fails while
parsing — inside the transaction, so it is rolled back, but with a `JsonException`. To verify.

## 4. Semantic decisions to make

### 4.1 Empty property names — recommendation: support them

How paths work today: segments are joined with `.`, a `.` or `\` inside a key is escaped as
`\.` / `\\` (`DiffPath`). `Split` cuts at unescaped dots; `Apply`/`ApplyReverse` split a path and
walk/create objects segment by segment; policy resolution walks parents by cutting at the last
unescaped dot (`TryGetParent`).

The format is **already unambiguous for empty keys** if empty segments are allowed: the empty key
at the top is the path `""`, below `a` it is `a.`, a key `""` below a key `""` is `.`. Since a path
always has at least one segment, `Split` and `Join` stay inverse functions; nothing collides with
the document root, which is never a path. The only reason empty keys fail today is that `Split`
rejects empty segments and the engines throw on purpose.

| Option | Paths of existing documents | Changes | Verdict |
|---|---|---|---|
| **1. Allow empty segments in the current format** | unchanged, byte for byte | `DiffPath.Split` (accept empty segments and the empty path), remove the empty-key throws, spec §2.1/§2.5, `diff.schema.json` path pattern (`+` → `*`), vectors: replace `empty-key-is-rejected` with vectors for `""`, `a.`, `.` | **recommended** |
| 2. JSON Pointer (RFC 6901: `/a/b`, `~0`/`~1`, `/` = empty key) | every path changes (`email` → `/email`) | the wire format, SQL queries on the feed (`diff ? 'email'`), policy and `WhenFieldChanged` paths, the `Reference` policy text `<type>/<id>/<path>` (then ambiguous to split), docs, vectors, F#/MCP/dashboard | standard, but buys nothing the escaped dot format cannot express once option 1 is done |
| 3. Reject consistently | unchanged | what the prototype does now: one reader pass over each input to find empty names anywhere, independent of what changed | consistent, but costs a full scan per diff and rejects valid JSON (e.g. a `Dictionary<string, T>` with an empty key) for a limitation the format does not have |

Effect of option 1 on the API: none on signatures. `Apply`/`ApplyReverse` work unchanged
(`JsonObject[""]` is a valid property). `ResolvePolicy` already handles `""` and `a.` correctly
(`TryGetParent("a.")` is `a`). A key index on an empty-named property would need a non-empty
index name. Backwards compatibility: no stored diff can contain such a path today (they were
rejected), so nothing to migrate; the library is not live anyway.

The prototype currently implements option 3 (the spec still says MUST reject) so that its
behavior does not depend on which subtrees are unchanged.

### 4.2 Duplicate property names — reject

`JsonDocumentOptions.AllowDuplicateProperties = false` (new in .NET 10) rejects duplicates at parse
time with a `JsonException`, including duplicates spelled differently (`"a"` and `"a"`) and in
unchanged subtrees — verified. `jsonb` collapses duplicates (last wins) before the kernel ever reads
a document, so only callers that hand UTF-8 to the engine directly can hit this. The current engine
fails too, but only when it reaches the duplicate (`JsonObject` throws `ArgumentException` lazily).

### 4.3 Insert and delete — keep, but fix §3.1

Treating an absent document as the empty object makes insert and delete exactly the update rule:
a field present on one side only is one entry with its whole value — the same as a field added
or removed in an update. So "no recursion on insert" is consistent with updates, not a special
case. The defect is in the policies (§3.1), not in the granularity.

### 4.4 Depth — one limit of 64

Proposal: `JsonLimits.MaxDepth = 64` everywhere (engine, prototype, explicit parser options in the
sessions, `CanonicalJson`, `PolicyProjector`), because 64 is what the serializer and the parser
enforce already and nothing larger can be written through the API. One documented exception type
for "too deep" at the boundary instead of today's mix (`NotSupportedException` from the engine,
`JsonException` from the parser). This is an API decision; not changed yet.

### 4.5 Root that is not an object — `ArgumentException`

The prototype throws `ArgumentException` naming the side (`before`/`after`). The current engine
takes `JsonObject` and cannot see such input; the sessions cast and would throw
`InvalidCastException`.

## 5. Prototype API notes

- No `ReadOnlyMemory<byte>?` parameters: a `null` `byte[]` converts implicitly to an **empty**
  memory, not to `null` — the first differential run turned every "absent" side into "empty
  input". The prototype has `Diff(before, after)`, `Insert(after)`, `Delete(before)` instead;
  the string overload keeps `null` for absent.
- Pooled arrays are returned in `finally` and cleared, so a pooled array never keeps a document
  reachable. (Not observable from a test without a pool seam; reviewed in code.)
- `WriteRawValue(skipInputValidation: true)` is used only on the result's own private, immutable
  buffer, filled from validated documents.

## 6. Benchmarks

### What is measured

`benchmarks/Papuma.Kernel.Benchmarks` (`-- diffpipeline`, `-- diffmemory`, `-- diffcold`, `-- diffverify`).
Three pipelines with **the same inputs and the same outputs**: a materialized `DocumentDiff` (what a
`SaveResult` carries), the tracked wire form as UTF-8, and the stored new state. Setup verifies for every
scenario that all three produce the same entries and the same feed (`diffverify`: all 17 agree).

- **current (text)** — today's update path: `::text` strings → `JsonNode.Parse` ×2 → `JsonDiffEngine.Diff` →
  `ToJson().ToJsonString()` → UTF-8 (what Npgsql sends).
- **prototype (text)** — the same strings into `Utf8JsonDiffEngine`, eager `ToDocumentDiff()`, `ToUtf8Json()`.
- **prototype (utf8)** — the same on UTF-8 bytes (a session that reads `jsonb` as bytes).

Inputs: 12 representative scenarios in `jsonb` text form (written by a formatter that follows the rules
pinned against PostgreSQL 18 in §2) and 5 adversarial ones whose after-side defeats the byte comparison:
reordered keys, every number respelled `n` → `n.0` (equal value, different bytes), pretty-printed, one
object with 20,000 shuffled keys, all non-ASCII escaped. **Warm** reads the same input every call;
**Cold** rotates through copies totalling more than the 18 MB last-level cache. BenchmarkDotNet 0.15.8,
in-process, pinned to one P-core (i7-1260P, Windows 11, .NET 10.0.11), 5 warm-up + 12 iterations.

### End to end (mean, warm; × = speed-up over current)

| Scenario | current | prototype (text) | prototype (utf8) | utf8, cold | allocated KB current / prototype | error |
|---|---:|---:|---:|---:|---|---:|
| Small1Change (0.3 KB) | 17.2 µs | 7.7 µs (2.2×) | 6.8 µs (2.5×) | 1.2× | 13.8 / 3.4 | ±29 % |
| Medium2Changes (3.4 KB) | 119 µs | 67 µs (1.8×) | 66 µs (1.8×) | 1.9× | 118 / 12 | ±12 % |
| LargeFewChanges (89 KB, 3 changes) | 7.6 ms | 1.57 ms (4.8×) | 1.33 ms (5.7×) | 5.1× | 2,725 / 195 | ±25 % |
| LargeManyChanges (2,500 changes) | 12.2 ms | 6.5 ms (1.9×) | 6.9 ms (1.8×) | 1.4× | 4,749 / 2,628 | ±18 % |
| Deep60 | 314 µs | 107 µs (2.9×) | 118 µs (2.7×) | 2.8× | 166 / 12 | ±12 % |
| LargeScalarArray (5,000 elements) | 4.0 ms | 2.5 ms (1.6×) | 2.4 ms (1.7×) | 1.2× | 2,715 / 569 | ±19 % |
| ArrayOfObjectsFieldChange (1,000 items) | 10.8 ms | 4.6 ms (2.4×) | 5.2 ms (2.1×) | 2.5× | 7,166 / 1,286 | ±34 % |
| ArrayInsertFront | 11.6 ms | 3.8 ms (3.1×) | 3.6 ms (3.2×) | 2.8× | 7,195 / 1,243 | ±11 % |
| Identical (89 KB) | 7.9 ms | 1.53 ms (5.2×) | 1.50 ms (5.3×) | 5.7× | 2,727 / 186 | ±15 % |
| Payload20Kb (one long string) | 21.7 µs | 20.3 µs (1.1×) | 10.8 µs (2.0×) | 2.0× | 46.8 / 1.9 | ±22 % |
| Unrelated | 10.1 ms | 5.2 ms (1.9×) | 5.2 ms (1.9×) | 1.8× | 3,233 / 1,910 | ±16 % |
| Insert (89 KB) | 11.0 ms | 6.8 ms (1.6×) | 7.6 ms (1.4×) | 2.2× | 3,975 / 2,142 | ±23 % |
| Adv: reordered keys | 8.6 ms | 3.0 ms (2.9×) | 2.9 ms (3.0×) | 3.2× | 2,710 / 634 | ±13 % |
| Adv: respelled numbers | 7.5 ms | 2.1 ms (3.6×) | 2.0 ms (3.7×) | 4.1× | 2,716 / 209 | ±16 % |
| Adv: pretty-printed | 7.5 ms | 2.5 ms (3.0×) | 2.5 ms (3.0×) | 3.3× | 2,750 / 504 | ±12 % |
| Adv: 20,000 shuffled keys | 44.2 ms | 15.5 ms (2.9×) | 15.3 ms (2.9×) | 2.8× | 12,227 / 3,184 | ±17 % |
| Adv: escaped non-ASCII | 5.9 ms | 3.6 ms (1.6×) | 3.9 ms (1.5×) | 2.0× | 1,925 / 710 | ±16 % |

Error is the half-width of the 99.9 % interval of the noisiest of the three; differences below ~1.3×
are not significant. Absolute times are about 2× higher than the June measurements of the README on the
same machine — compare ratios, not microseconds.

### Where the time goes (phases, warm)

| Scenario | C1 lazy parse | C2 + build trees | C3 diff | C4 feed | P1 parse + validate | P2 diff | P3 feed | P4 materialize |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| LargeFewChanges | 860 µs | 5,507 µs | 2,465 µs | 1 µs | 1,348 µs | 154 µs | 0.4 µs | 1 µs |
| LargeManyChanges | 829 | 5,505 | 3,867 | 2,961 | 1,494 | 1,506 | 419 | 2,303 |
| Identical | 1,046 | 5,341 | 2,281 | 0.2 | 1,430 | 4.6 | 0.1 | 0 |
| Insert | 423 | 2,682 | 3,116 | 4,259 | 701 | 1,491 | 363 | 2,421 |
| ArrayOfObjectsFieldChange | 1,405 | 3,726 | 1,781 | 2,932 | 2,177 | 479 | 164 | 1,293 |
| Adv: reordered keys | 948 | 5,735 | 3,092 | 1 | 1,587 | 1,296 | 0.4 | 1 |

The validation inside P1, measured separately on the 89 KB document (one side): parsing alone 301 µs,
**duplicate rejection +206 µs, the empty-key scan +260 µs**. Validation more than doubles the parse.

### Memory (one call; `-- diffmemory`)

| Scenario | allocated KB (current / prototype utf8) | pooled KB rented | retained by the result KB |
|---|---|---|---|
| Medium2Changes | 121 / 12 | 63 / 28 | 44 / 1.3 (+3.4 stored text) |
| LargeFewChanges | 2,819 / 194 | 1,391 / 780 | 1,055 / 1.8 (+89) |
| LargeManyChanges | 4,835 / 2,644 | 1,615 / 3,427 | 1,733 / 1,357 (+105) |
| ArrayOfObjectsFieldChange | 7,367 / 1,336 | 1,521 / 1,538 | 3,516 / 762 (+101) |
| Unrelated | 3,262 / 1,908 | 497 / 1,612 | 1,152 / 976 (+35) |
| Insert | 4,049 / 2,201 | 753 / 2,241 | 1,681 / 1,221 (+89) |

"Allocated" agrees with the diagnoser's column above. "Pooled" are bytes rented from `ArrayPool` during
the call (reused memory, not allocated — `JsonDocument` metadata, and for every `JsonNode.Parse` of a value
another small document). "Retained" is what stays reachable through the result after a full GC: for the
current pipeline that is mostly the node tree of the new document behind `SaveResult.Stored` (~12× the
document's text size), for the prototype the changed values; it keeps the new state as text instead (in
brackets). The prototype allocates less in every scenario; with thousands of entries it rents more from the
pool and retains nearly as much, because the eager `ToDocumentDiff()` parses every value into its own
`JsonNode` (P4). (A first version of this probe listened to pool events while counting allocations; the
listener's own allocations made the prototype look worse. Fixed: events are on for the pooled call only.)

### First call (fresh process, JIT; `-- diffcold`, median of 7)

| Scenario | current | prototype (text) | prototype (utf8) |
|---|---:|---:|---:|
| Small1Change | 17.6 ms | 25.5 ms | 24.6 ms |
| LargeFewChanges | 49.4 ms | 52.3 ms | 51.1 ms |

The prototype costs ~7–8 ms more JIT once per process; the second call (still tier-0 code) is ~1.2–2 ms
for both. Irrelevant for a long-running service, visible for short-lived processes.

### What this shows — and where the comparison is not fair

- The prototype is faster in **every** scenario, adversarial ones included, by 1.1–5.7×, and allocates
  less in every scenario (1.7× less with thousands of entries, 4–25× less with few changes).
- The gain is **smaller than the first estimate (3–12×)**: that prototype neither validated (duplicates,
  empty keys) nor materialized `DocumentDiff`. Both cost real time here.
- The main win is not skipping bytes but **not building node trees**: with the byte comparison defeated
  (adversarial rows) the prototype is still ~3× faster; with it (Identical) ~5×.
- After that, the prototype's cost is parse + validation (few changes) or materialization (many changes).
  Supporting empty keys (§4.1) would remove the scan; a lazily materialized `DiffEntry` would remove P4.
- Warm and cold data differ less than the noise: the inputs are read sequentially, which the hardware
  prefetcher hides. Not a claim about random-access-heavy workloads.
- UTF-16 input costs the prototype a transcoding pass; it matters only for string-heavy documents
  (Payload20Kb: 1.1× from text, 2.0× from UTF-8).
- Not fair, in the current pipeline's favor: no policies (a `Hash` policy needs the value as a node, a
  split entry (§3.1) more work), the prototype's stored state is text — `GetDocument<T>()` would deserialize
  from text instead of from the node tree (not measured). Not fair in the prototype's favor: reading
  `jsonb` as bytes from Npgsql (skip the version byte) is assumed, its cost is not in the utf8 column.
- In-process measurement, one laptop, synthetic data in `jsonb` form rather than a dump of real
  documents. The ±10–35 % errors survive pinning to a P-core; production-like Linux hardware is needed
  before quoting absolute numbers.

## 7. Remaining risks and not covered yet

- Policies are not applied by the prototype pipeline; the benchmarks use a model without
  policies. A `Hash` policy needs the value as `JsonNode` (canonical form), so a policed diff
  materializes those values anyway.
- `Papuma.Kernel.Local` (SQLite) stores the text as written, not canonical: the fast path hits
  as long as the same type serializes the same way (stable property order), which is the normal
  case but untested.
- The eager `ToDocumentDiff()` dominates the prototype's cost when there are many entries (P4 in
  §6); a lazily materialized `DiffEntry` would change the public type's internals.
- Measured on one laptop (i7-1260P, Windows); absolute numbers need a run on production-like
  hardware, ideally Linux.
