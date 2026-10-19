# ADR-028 — Logical type names are declared: `[DocumentType]`, `[EventType]`, `Named`

## Status

Accepted (2026-10-19) — makes explicit what [ADR-002](adr-002-document-as-truth.md) and the
specification already assume: a document type has a *logical name*, not a language-specific one.
Both kernels. Compatible with every existing model (the default is unchanged).

## Context

A type's name is stored in every row (`document_type`, `event_type`) and from there reaches the key
index names, `Reference` policy text (`User/123/email`), the hash key derivation
([ADR-025](adr-025-keyed-hash-policy.md)), metrics tags, exports and the NATS subject of the bridge
recipe. Until now it was always `typeof(T).Name`, with no way to set it. Three consequences, one of them
found by running a probe (see `docs/analyses/wire-format-gaps-before-live.md`):

1. **Renaming or moving a class is a data migration.** The rows keep the old `document_type`; the new
   class looks for the new one and finds nothing. Nothing fails — the documents are simply gone.
2. **Two classes with the same simple name collided silently** (`SalesArea.Item`, `StockArea.Item`): both
   built, both mapped to `document_type 'Item'`. That is fixed by a uniqueness check at build time; but a
   check can only *reject* a collision, it cannot offer a way out.
3. **A second implementation inherits a C# detail.** A Rust or Python implementation must agree on
   `document_type` with the .NET one; it should agree on a *declared* name, not on how C# names a class.

## Decision

1. **`[DocumentType("name")]` and `[EventType("name")]`** on the class declare the logical name; the fluent
   `Document<T>(d => d.Named("name"))` / `Event<T>(e => e.Named("name"))` does the same and **wins** over
   the attribute (attributes are the default at the place of truth, fluent configuration overrides — the
   rule of [ADR-007](adr-007-privacy-policies.md)).
2. **The default is unchanged:** without a declaration the name is the simple class name. No existing model
   changes, no stored row changes, no migration. Declaring a name for a type that already has rows under its
   class name means choosing that same string, or migrating the column.
3. **The pattern is unchanged:** `[A-Za-z][A-Za-z0-9_]{1,100}`. The SQLite kernel writes the name into SQL as
   a validated literal (to match its partial key indexes), so widening the pattern would be a security
   decision rather than a naming one. A name is therefore `customer` or `CustomerAccount`, not `customer-account`.
   An invalid name fails when the model is built.
4. **Uniqueness stays a build-time rule:** document names are unique among document types, event names among
   event types, compared case-insensitively (key index names are lower-cased). The error now says how to
   resolve a collision: give one type a different declared name, or rename a class.
5. **Not mandatory.** Requiring a declaration on every type would tax every application for a problem only
   some have. The documentation recommends declaring names for any type that is expected to outlive a
   refactoring, or to be shared with another implementation.

## Consequences

- **Positive:** A class can be renamed, moved to another namespace or split from a file without orphaning its
  documents, changing its key index names, or changing the hash key of its fields. Tests prove it on both
  engines: a renamed class with the same declared name loads and updates the predecessor's documents.
- **Positive:** Two same-named classes now have a way to coexist.
- **Positive:** The wire contract can say "the declared logical name" and mean it for every implementation.
- **Negative:** A declared name is a promise: changing it later *is* a migration, exactly as changing the class
  name was before — the difference is that it can now be a deliberate act instead of a side effect of
  refactoring.
- **Negative:** Nothing stops an application from leaving every type undeclared, and then nothing changes. The
  protection is opt-in by design.
- **Neutral:** Attribute and fluent name are read once at model build; there is no runtime cost.

## Alternatives considered

- **Mandatory declaration.** Safest against refactoring, but boilerplate on every type, and it breaks every
  existing model. Rejected for now; revisit if a real incident shows that "recommended" is not enough.
- **A namespaced default (`Company.Sales.Item`).** Removes most collisions but changes every stored name, makes
  identifiers longer, and would need dots in the name pattern — which this codebase just spent effort making
  harmless in *paths*. Rejected.
- **A relaxed name pattern (`user-account`).** Nicer for humans, but the pattern guards SQL interpolation on
  SQLite. Not worth the risk.
- **A rename table that maps old to new names.** Solves migration, not identity, and adds state the kernel would
  have to keep consistent. The declared name makes the rename unnecessary in the first place.

## Related

- [ADR-002](adr-002-document-as-truth.md) — the document and its type are the source of truth.
- [ADR-006](adr-006-keys-and-constraints.md) — key index names derive from the type name.
- [ADR-007](adr-007-privacy-policies.md) — attribute default, fluent override.
- [ADR-025](adr-025-keyed-hash-policy.md) — the hash key is derived per tenant and type name.
- The specification, `docs/spec/changes.md` §1: logical names, unique per model.
