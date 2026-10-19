# Papuma Spec — Documents, Diffs and Policies

Status: **draft 0.1** · normative language: MUST, MUST NOT, SHOULD, MAY as in RFC 2119

This chapter specifies what a conforming implementation computes when a document
changes: the **diff**, the **policies** applied to it, and the **change record**
that carries the result. Background and rationale: [ADR-002](../adr/adr-002-document-as-truth.md),
[ADR-004](../adr/adr-004-changerecord-diff-only.md), [ADR-007](../adr/adr-007-privacy-policies.md),
[ADR-008](../adr/adr-008-rollback-is-update.md). Test material: [vectors/](vectors/).

## 1. Documents

A document is a JSON **object**. Its identity is `(scope, tenantId, documentType, documentId)`.
`documentType` is a logical name declared in the metamodel — an implementation MUST
NOT derive it from a language-specific type name that the other implementations
cannot reproduce. Each write increments `version` by exactly one, starting at 1.

For diffing, an *absent* document (before an insert, after a delete) is
equivalent to the empty object: every top-level field is reported as added
(insert) or removed (delete) with its whole value, without recursion.

## 2. Diff computation

`Diff(before, after)` produces a map from **path** to **entry**.

1. **Paths** are dot-separated object keys: `address.city`. Array indices are never
   part of a path.
2. If a key's value is an object on **both** sides, the diff recurses into it.
3. Any other difference is **one** entry at that path carrying the complete old and
   new value. This covers arrays (atomic), scalars, and any change of JSON type —
   object → scalar, scalar → object, object → null, object → array. An
   implementation MUST NOT recurse across a type change; doing so breaks
   reversibility.
4. Values are equal when they are equal as JSON values. Object key order is
   irrelevant. **Numbers are equal when they have the same mathematical value**,
   at arbitrary precision and independent of their text form: `1`, `1.0` and
   `1e0` are equal; `0` and `-0` are equal; `9007199254740992` and
   `9007199254740993` are different, as are `0.1` and `0.10000000000000001`
   (they would collide as IEEE-754 doubles). An implementation therefore MUST NOT
   compare numbers as binary floating point. (Vectors: `numbers-*`.) A change
   between two equal numbers produces no entry; the text form of the value that
   is stored is the writer's.
5. An object key containing `.` cannot be represented as a path. An implementation
   MUST reject the document change with an error rather than emit an ambiguous
   path. (Vector: `dotted-key-is-rejected`.)
6. Producers SHOULD emit entries ordered by comparing paths as sequences of
   UTF-16 code units (.NET `StringComparer.Ordinal`). Beware: comparing UTF-8
   bytes (the natural order in Rust or Go) differs for characters outside the
   Basic Multilingual Plane. Consumers MUST NOT depend on entry order.

### Entry shape (tracked)

```jsonc
{ "old": <value>, "new": <value> }   // changed
{ "new": <value> }                    // field added  (no "old" key)
{ "old": <value> }                    // field removed (no "new" key)
```

**Presence of the key encodes existence; the value may be JSON `null`.**
`{"old": null, "new": "x"}` (changed from null) and `{"new": "x"}` (added) are
different facts and MUST stay distinguishable. At least one of `old` / `new` MUST
be present.

### Reversibility

For every tracked diff: `Apply(before, diff) == after` and
`ApplyReverse(after, diff) == before`, where an absent document is the empty object.
`Apply` sets or removes each path (creating intermediate objects as needed).
A diff containing a non-tracked entry (§3) MUST NOT be applied: the operation MUST
fail rather than reconstruct a state with missing values.

## 3. Policies

Policies are declared per document type and path and applied **when the diff is
produced**, before the record is stored. No code path may store an unpoliced diff.

| Policy | Entry in the stored diff |
|---|---|
| `Track` (default) | the tracked entry unchanged |
| `Redact` | `{ "changed": true }` |
| `Reference` | `{ "ref": "<documentType>/<documentId>/<path>" }` |
| `Hash` | `{ "changed": true, "hash": "<hex>" }`; no `hash` key when the field was removed |
| `DoNotTrack` | no entry at all (the diff may become empty) |

**Resolution.** The effective policy of a diff path is that of the exact path if
declared, otherwise of its nearest declared ancestor (a policy on `address` covers
`address.city`), otherwise `Track`. The `Reference` text always uses the
*diff path* (`User/123/address.city`), never the declared ancestor.

**Non-tracked entries are not values.** They carry no data to reconstruct state
from. A foreign consumer MUST treat them as "this field changed", nothing more.

### The Hash policy is keyed; v1 is read-only

The hash is `v2:<kid>:<hex>`: an HMAC-SHA-256 over the canonical form of the new value, under a key
derived per tenant and type ([canonical-json.md](canonical-json.md),
[ADR-025](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/adr/adr-025-keyed-hash-policy.md)).
An implementation MUST NOT write a hash without a key: a `Hash` policy that applies when no key is
configured is an error (policy vector `hash-without-a-key-fails`).

Records written before the keyed hash carry **v1** values — a bare 64-digit lowercase hex string, the
SHA-256 of the value's serializer-specific JSON text. That form was neither canonical nor salted, is
**read-only**, and is never produced again. History is not rewritten. A consumer treats a hash as an
opaque string and compares only hashes of the same form: a v1 and a v2 hash of the same value never
match, nor do two key ids.

## 4. Change record

The logical form is [schemas/change-record.schema.json](schemas/change-record.schema.json).
Notes beyond the schema:

- `seq` is the identity of a record within one store. It is **not a cursor and not
  a watermark**. Consumers MUST NOT skip everything below the highest `seq` seen:
  on a store with concurrent writers a lower `seq` can be delivered after a higher
  one.
- Within one document, records are delivered strictly by `version`, gapless.
- Delivery is **at-least-once**; consumers MUST be idempotent. `(documentType,
  documentId, version)` is a natural idempotency key.
- `schemaVersion` describes the shape of the diff content. History is never
  rewritten when the schema evolves; consumers handle historical shapes.
- `operation` is `1` insert, `2` update, `3` delete. A rollback is an update with
  rollback markers in `metadata` ([ADR-008](../adr/adr-008-rollback-is-update.md)).

## 5. Conformance

See [README.md](README.md#conformance).

## 6. Findings while writing this chapter

Kept here so they are not lost; each is also in the roadmap.

- The hash scheme (§3) is the only place where the *value text* of a document, not
  its JSON value, leaks into the contract.
- Number equality (§2.4) was measured, not designed: the .NET engine compares by
  decimal value and keeps the original text. That is the strictest sensible rule,
  but it costs a second implementation an arbitrary-precision comparison (in Rust,
  `serde_json` needs its `arbitrary_precision` feature and a decimal compare). It
  is recorded here as the draft decision so it can be challenged before a second
  implementation relies on it.
- Because the original number text is preserved, two stores can hold the *same*
  number in different text forms (expected, not yet verified end to end:
  PostgreSQL `jsonb` normalises exponent forms, SQLite keeps the text). Equality
  is by value, so diffs agree; only the Hash
  policy (§3) is affected, since it hashes the text.

## 7. Open points (not normative yet)

- Number range outside the hash: equality is by value (§2.4), and the canonical form used by
  the hash caps a number's canonical text at 1024 characters ([canonical-json.md](canonical-json.md#12-numbers)),
  but documents themselves accept `1e400` and larger. Whether to cap them there is open.
- Ordering and delivery as a stand-alone chapter, including what a local, single
  writer store may promise beyond what a concurrent one can.
- The event log record and its payload policies.

(Resolved since the first draft: the patch operation catalog is in [patch.md](patch.md), canonical
JSON and the keyed hash in [canonical-json.md](canonical-json.md).)
