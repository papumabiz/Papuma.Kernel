# ADR-026 — Read all documents behind a declared key: `LoadAllByKeyAsync`

## Status

Accepted (2026-10-19) — extends [ADR-006](adr-006-keys-and-constraints.md) and
[ADR-020](adr-020-composite-keys.md) (declared keys get a read that returns what the index
finds). Implemented on both kernels.

## Context

ADR-006 lets a model declare `UniqueKey` and `LookupKey` (single field, or composite since
ADR-020), and both kernels materialize a partial expression index for each. The only read that
uses them is `LoadByKeyAsync`, which returns **one** document or `null` and throws when the key
matches more than one. So a `LookupKey` — the non-unique kind — has an index and no read that
returns what the index finds; `LoadManyAsync` takes ids, not a key.

The need is "the current documents of one type behind one declared field": all messages of a
thread, all invitations for an email. [concepts §16](../concepts.md) sends such a read to a
projection or an SQL view. Both fail exactly where this need is sharpest. A projection of a
document with a `[SensitiveData]` field copies that field into a table outside the policy
regime (erasing the document would not clear the copy) and adds feed lag, so a user does not see
their own message at once; a view is PostgreSQL only and §16 forbids it for sensitive fields.
The consumer that raised this (a chat-style application) therefore read the kernel's `document`
table with its own SQL on both engines — a bet on a layout the kernel does not promise.

**Why this is not the query-DSL trap.** ADR-006 and ADR-016 reject "search documents by field".
This asks for nothing a declared key does not already say: no field outside a declaration, no
operators, no combinators. It is `LoadByKeyAsync` for a key that is not unique.

## Decision

### 1. One new session read

`LoadAllByKeyAsync<T>` on `DocumentSession` and `SqliteDocumentSession`, same signature on both:

```csharp
IReadOnlyList<DocumentResult<Message>> page =
    await session.LoadAllByKeyAsync<Message>(x => x.ThreadId, threadId, limit: 500, afterId: null, ct);
```

It takes the same key forms as `LoadByKeyAsync` — single field, and the composite overload with
one value per component — and resolves them through the same declared-key lookup, so an
**undeclared key throws** with the existing message. Unique and lookup keys are both allowed.

### 2. Bounded, ordered by id, paged

`limit` is required, has no default and must be between 1 and `KeyMetadata.MaxLoadAllLimit`
(1000): there is no unbounded read. Order is ascending `id`; `afterId` continues from the last id
of the previous page. No other ordering, no predicate.

**The order is ordinal (binary) on both engines**: PostgreSQL compares with `COLLATE "C"`, SQLite
with its default `BINARY`. Without that the same ids would page differently per engine (database
collation versus byte order). Ids are application-chosen, so the order is meaningful only to an
application whose ids sort in that sense; the contract is "stable and ordinal by id", nothing more.

### 3. The guarantees of `LoadByKeyAsync`

Scope-bound (the explicit scope predicate, and RLS on PostgreSQL); run inside the session's
transaction, so the session sees its own uncommitted writes; documents come back through the same
deserialization path as `LoadAsync`, so the upcaster pipeline applies (which a view cannot do);
and **full clear-text state, exactly as `LoadAsync` returns it**. Policies act on the feed and on
masked reads (ADR-007, ADR-016); this is the in-process business-logic read like the others, so
the sensitive-field objection to views does not apply.

### 4. It uses the declared key's index; the ordering is a separate step

The match runs against the existing key index and nothing new is materialized. The index does
**not** deliver id order, so the engine sorts the matches, then applies `afterId` and `limit` —
cheap for the intended sizes (hundreds per key), and stated so that nobody reads "index-backed"
as "ordered by the index". A declared key without an index on the target engine is a bug, not a
fallback to a scan, and **tests pin it**: `EXPLAIN` on PostgreSQL and `EXPLAIN QUERY PLAN` on
SQLite must show the key index, with and without `afterId`.

SQLite needed care to get there, found by running the prototype: the key index is partial
(`WHERE document_type = '…'`), which SQLite cannot match against a bound parameter, so the type
is a validated literal; and `id > ?` / `ORDER BY id` pull the planner onto the primary key, where
the key predicate degrades to a filter over every document of the type in the tenant, so `id` is
wrapped in a unary plus. PostgreSQL needed neither. (The same literal-type problem existed in
`LoadByKeyAsync` on SQLite and was fixed separately, `000120f`.)

### 5. Out of scope, on purpose

Any field that is not a declared key; sorting by anything but id; counts and aggregates; joins;
"all documents of a type" with no key; keys over array elements. Each is named so the next
request has an answer ready. The first is the line ADR-016 draws.

## Consequences

- **Positive:** A lookup key becomes usable for what its name says, with no new index and no new
  schema.
- **Positive:** A consumer with sensitive fields and a read-after-write requirement has a
  supported path on both engines instead of a view (PostgreSQL only) or a copy (ADR-007's
  reasoning forbids it).
- **Positive:** The ordinal id order is the same on both kernels and is covered by a test with ids
  that sort differently under a locale collation (`B1`, `a1`, `b2`).
- **Negative:** One more public read to document and keep identical on two kernels.
- **Negative:** The sort is not free: a key that matches many thousands of documents sorts them
  on every page. The limit of 1000 bounds the result, not the work behind it.
- **Negative:** On SQLite a `Local` session holds the writer's lock while it is open, so this read
  waits behind writers and other readers. A read-only session (WAL snapshot, no writer lock) would
  fix that for every read; it is its own proposal and not a prerequisite.
- **Neutral:** It does not make the store queryable. Everything §16 says about projections as the
  default for derived reads stays true; step 1 ("a single document, current") widens from one
  document to the documents behind one declared key, and no further.

## Alternatives considered

- **Keep reading the table with application SQL.** Works, but depends on a layout the kernel does
  not promise and, on SQLite, on a connection the application had to build itself.
- **Make the table layout a public contract.** Turns the schema into an API that may not change,
  which ADR-005 and ADR-020 show the kernel does not want to be.
- **A projection per read.** The kernel's own default; rejected here for sensitive text copied
  outside the policy regime and for lag against read-after-write.
- **A composite index `(key, id)` so the index delivers the order.** Avoids the sort but doubles
  the index cost of every lookup key to speed up a case that is small by construction. Revisit if
  a real key turns out large.
- **A query builder over declared keys.** Rejected as ADR-006 and ADR-016 reject it.
- **A default or unbounded `limit`.** An unbounded read is exactly what the declared-key
  discipline avoids; the caller states the page size.

## Related

- [ADR-006](adr-006-keys-and-constraints.md), [ADR-020](adr-020-composite-keys.md) — the
  declared keys this read uses.
- [ADR-007](adr-007-privacy-policies.md), [ADR-016](adr-016-policy-projected-reads.md) — why a
  clear-text business-logic read is allowed here, and where the line to a query DSL is drawn.
- [concepts §16](../concepts.md) — projections as the default for derived reads.
- Open and deliberately separate: a masked variant for untrusted consumers (after ADR-016), a
  read-only session on `Local`, and "list a type within the scope" without a key.
