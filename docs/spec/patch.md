# Papuma Spec — Patch Operations

Status: **draft 0.1** · companion to [changes.md](changes.md) · vectors: [vectors/patch.json](vectors/patch.json)

A *patch* changes single fields of a stored document without the caller loading it
([ADR-012](../adr/adr-012-partial-updates.md)). The diff, policies and change record
that result are exactly those of a full save ([changes.md](changes.md)); this chapter
specifies only how the **new document state** is derived from the old one.

## 1. Operations

The catalog is deliberately closed: `set`, `remove`, `increment`. Anything else is
load + save. A patch is an **ordered list** of operations (at least one) applied to
one document. Paths are arrays of object keys (`["address", "city"]`); array indices
are not addressable.

| Operation | Effect |
|---|---|
| `set(path, value)` | The field at `path` becomes `value` (any JSON value, including `null`). Replaces an existing value of any type; adds the field if absent. The parent object must exist (§2). |
| `remove(path)` | The field at `path` is deleted. Removing an absent field, or a field below an absent parent, is a no-op. |
| `increment(path, by)` | The field becomes its numeric value plus `by`. An absent field or JSON `null` counts as `0`. `by` is a signed 64-bit integer; the result is exact (no overflow at 64 bits, no binary floating point). |

### Order

Operations apply **in list order, each seeing the result of the previous one**. So
`set a=10` then `increment a by 5` yields `15`, and two increments on one path
accumulate. (Both engines conform; see §3 for why this is pinned.)

## 2. Parents and types

- `set` and `increment` address a field whose **parent object must already exist**.
  If an intermediate key is absent, or is not an object (scalar, array, `null`), the
  operation **fails** (`parent-missing`; `PatchTargetMissingException` in the .NET
  kernels) and the whole patch is rolled back. A patch never creates or replaces a
  parent; to build a nested value, `set` the parent first (a later operation sees it).
  `remove` is different: removing something that is not there, or below a missing
  parent or below an array, is a no-op, so a retried removal stays idempotent. (An array is not
  addressed by property name: a key that merely *looks* like an index, such as `"0"`, does not reach
  into it, for `set`, `increment` or `remove`.)
- `increment` on a value that is neither absent, `null`, nor a number **fails**
  (`not-numeric`; `NonNumericIncrementException` in the .NET kernels) and the whole
  patch is rolled back. It MUST NOT overwrite or coerce the value — a numeric string
  such as `"5"` is not a number.
- A patch whose **result** would nest deeper than 64 levels (changes.md §2.5) — a deep
  value set far down, each fine on its own — **fails** (`too-deep`;
  `DocumentTooDeepException` in the .NET kernels) and the whole patch is rolled back. A
  value that is too deep on its own fails before anything is sent.

### When several operations would fail

A patch that fails changes nothing, whichever operation fails. If more than one operation would fail
(say a missing parent *and* a non-numeric increment), **which error is reported is unspecified**: the
in-process definition stops at the first failing operation, PostgreSQL's SQL raises its own errors before
the missing-parent check runs. Both fail, with a typed exception, and roll back. The differential test
asserts that success, failure and the resulting document agree — not the error kind.

## 3. History: divergences found and fixed

The first run of [vectors/patch.json](vectors/patch.json) against the real PostgreSQL
expression builder and the SQLite applier (2026-10-19) found six cases where the two
kernels disagreed, two of which silently lost an update and two of which silently
overwrote data. All were fixed the same day (see the changelog); the vectors now pass
unchanged on both engines. Kept here as the reason the vectors exist:

| Case | PostgreSQL before | SQLite before |
|---|---|---|
| two increments on one path | lost one (`1 +2 +3` gave `4`) | correct |
| `set`, then `increment` on one path | discarded the `set` (`6` instead of `15`) | correct |
| `increment` on a string or object | raw `PostgresException` (numeric strings were coerced) | overwrote the value with `by` |
| `set` / `increment` below a missing parent | did nothing | created the parents |

Both now fail with `parent-missing` (§4).

| `set` below a scalar parent | did nothing | replaced the scalar with an object |

Causes: PostgreSQL's `increment` read `data #>> path` from the stored column instead of
the expression built so far; SQLite's `JsonPatchApplier` created missing parents for
every `set`/`increment` and treated any non-number as `0`.

A vector case may still carry a `knownDivergence` object (`{ "<engine>": { "after": … } }`)
to pin a result an engine produces *temporarily* instead of the target. The runners
assert the pinned value, so a later fix turns the test red until the vector is updated.
None is in use.

## 4. Decisions taken

1. **Missing / non-object parent is an error** (2026-10-19). Both engines used to differ
   (PostgreSQL did nothing, SQLite created the parent); doing nothing hides typos in
   paths and still bumped the version with an empty diff. PostgreSQL cannot raise from
   inside a `jsonb_set` expression without a schema function, so it checks the rule
   in-process against the `old.data` the statement already returns
   (`JsonPatchApplier.EnsureTargetsExist`) and rolls back; SQLite applies the same
   class directly. One known gap: on PostgreSQL, a path segment that looks like an
   integer (`"0"`) below an *array* parent is interpreted by `jsonb_set` as an array
   index and applies; no C# property name produces such a segment.
2. **`increment` is exact** and works on absent, `null` or numbers only (§2).

## 5. Open points

- Range of `increment` results: the in-process applier computes in `decimal`; a stored
  number beyond `decimal` range is reported as non-numeric by SQLite, while
  PostgreSQL's `numeric` has no such limit. Unobserved in practice; see
  [changes.md](changes.md#7-open-points-not-normative-yet).
