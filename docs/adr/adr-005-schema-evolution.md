# ADR-005: Schema evolution via schema_version and an upcaster pipeline

## Status

Accepted (2026-06-11)

## Context

When the C# class defines the truth but the database holds JSONB documents from
earlier class revisions, exactly the migration problem arises — without a concept
— that Papuma Kernel is fleeing from: renamed properties, changed types, restructured
nesting break deserialization silently or loudly. This is the best-known pain
point of document stores (cf. Marten upcasting). The original design discussion had
no answer for it — hence this day-one ADR.

## Decision

1. **Every document and every ChangeRecord carries `schema_version`** (int, per
   document type).
2. Per type, **upcasters** are registered that lift raw JSON from version n to
   n+1:

   ```csharp
   builder.For<User>()
       .Upcast(fromVersion: 1, json =>
       {
           json["email"] = json["mail"];
           json.AsObject().Remove("mail");
       })
       .Upcast(fromVersion: 2, json => /* ... */);
   ```

   The current schema version of a type is `highest fromVersion + 1`; without
   upcasters it is 1.
3. **Upcasting happens at load time (lazily)**, on the raw `JsonNode`, before
   deserialization. There is no big-bang data migration.
4. **The new state is persisted only on the next save** (the update writes
   `schema_version` along). Optionally, a maintenance worker can proactively lift
   documents via load+save — that is an operations decision, not a kernel mandate.
5. **ChangeRecords are never migrated retroactively.** They keep the
   `schema_version` of their creation time. During feed replay (rebuild), the
   engine can optionally apply the upcaster pipeline to diff values; consumers
   that do not use this must cope with historical schemas.
6. **Prohibitions the kernel enforces**: a save with a lower `schema_version` than
   the stored one is an error (prevents old deployments from silently migrating
   new documents backwards).
7. **Additive changes need no upcaster and no version bump.** Changes that do not
   touch the semantics of existing data are covered by JSON deserialization
   itself:

   - a new optional property with a **constant** default,
   - removing a property (surplus JSON fields are ignored; they disappear on the
     next save),
   - adding a new enum value.

   Upcaster + version bump are only needed for **transforming** changes: rename,
   restructuring, type change, derived (non-constant) defaults. Explicitly
   forbidden is the **additive simulation of transformations** (a new field next
   to the old one, readers check "if `email` is empty, take `mail`") — that
   spreads compatibility logic permanently across all consumers. The rule keeps
   upcaster chains short; it does not replace upcasters.

## Rejected alternative: Protobuf

Protobuf was evaluated as the evolution mechanism and rejected:

- **Opaque blob instead of JSONB**: Protobuf storage (`bytea`) loses everything
  Papuma Kernel stands on — expression indexes (ADR-006), SQL-inspectable documents,
  queryable diffs, policies on JSON paths (ADR-007).
- **Tolerance instead of transformation**: Protobuf's model (field numbers,
  preserving unknown fields) prevents read errors but cannot establish semantics —
  a rename without a kept field number is silent data loss, restructurings are
  structurally impossible. Upcasters perform arbitrary, testable transformations.
- **An IDL hybrid is not worth it**: `.proto` as the schema source with JSON
  mapping would shift the truth from the C# class into protoc-generated types and
  break the attribute-based policy model.

What is adopted instead is Protobuf's **discipline** (point 7: additive as the
default). At the integration edge — e.g. when publishing translated domain events
to external consumers (ADR-011) — Protobuf remains a good choice; that is handler
business, not kernel business.

## Consequences

- Class changes are a normal, tested procedure: write the upcaster + bump the
  version, done. Upcasters are pure functions over JSON and thus trivially
  testable.
- Lazy upcasting means: old documents may lie around in their old state
  arbitrarily long. Upcasters must therefore never be removed as long as documents
  of their source version can exist.
- Point 6 demands disciplined deployments (no application rollback underneath
  already-lifted documents) — but the failure is then loud instead of silent.
