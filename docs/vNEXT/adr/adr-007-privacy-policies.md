# ADR-007: Privacy policies — attributes as defaults, fluent as override

## Status

Accepted (2026-06-11)

## Context

Because the kernel knows the C# model (types, properties, attributes), it can
apply privacy rules to the change feed automatically — before personal data ever
reaches an immutable feed. That is vNEXT's unique selling point over outbox and
event-sourcing systems, which this problem catches up with painfully years later.

The vNEXT discussion ended at "fluent configuration only" (because policies are
organization-dependent). Against that speaks: the model is the place of truth —
whoever reads the class should see that `Email` is sensitive.

Prior work from v1 that is adopted conceptually: the
sensitive-data-reference-pattern ADR (removed with the v1 cleanup) — a hybrid
model with references in the feed, a versioned sensitive data store, and explicit
opt-in resolution instead of invisible magic.

## Decision

1. **Policy catalog** (effect on a field's diff entry, cf. ADR-004):

   | Policy       | Attribute         | Diff entry                            |
   |--------------|-------------------|---------------------------------------|
   | `Track`      | — (default)       | `{ "old": ..., "new": ... }`          |
   | `Redact`     | `[SensitiveData]` | `{ "changed": true }`                 |
   | `Reference`  | `[TrackReference]`| `{ "ref": "User/123/email" }`         |
   | `Hash`       | `[TrackHash]`     | `{ "changed": true, "hash": "..." }`  |
   | `DoNotTrack` | `[DoNotTrack]`    | field does not appear in the diff     |

2. **Two sources, clear priority**: attributes on the class set the default;
   fluent configuration at store setup overrides per organization/deployment:

   ```csharp
   builder.For<User>()
       .Property(x => x.Email).StoreAsReference()   // override: Redact → Reference
       .Property(x => x.LastLoginIp).DoNotTrack();
   ```

3. **Policies act when the diff is produced**, in the same transaction as the save
   (ADR-003). There is no downstream scrubbing process for new changes.
4. **References never resolve automatically.** Change handlers that need the value
   resolve explicitly via a resolver API (opt-in) — against the current document
   state or the sensitive data store. If the document is GDPR-deleted, references
   dangle; the feed stays free of content.
5. **Delete diffs respect policies**: even the last `old` of a sensitive field
   appears only as `changed`/`ref`, never as plain text.
6. The metamodel (including policies) is built **once at startup** (reflection,
   later optionally a source generator) — runtime cost per save is lookups, not
   reflection.

## Consequences

- GDPR erasure = delete the document; the feed does not need to be touched.
- The class documents the default sensitivity; per-organization compliance
  deviations are possible without recompiling (fluent).
- Handlers that need sensitive values are recognizable as such in code (explicit
  resolver call) — auditable instead of magical.
- The sensitive data store (versioned offloading) is only needed when historical
  sensitive values are required beyond the document lifecycle; for the start, the
  reference pattern against the document itself suffices.
- **Closure (2026-06-12): there will be no kernel-managed sensitive store.**
  References resolve against the *current* document state only; once a field
  changes or the document is deleted, the historical value is deliberately
  unrecoverable — that is data minimization working, not a gap. The original v1
  motivation is superseded by phase 12 (`RedactHistoryAsync` as the safety net,
  the inventory as the review tool, the policy-applied export). Applications
  with a *legitimate* need for versioned sensitive values have state worth
  remembering — and state belongs in documents: model the history as its own
  document type with its own policies, retention and erasability. A kernel
  store would add key management, another RLS surface and integration duties
  in redaction/export/inventory for a need the existing primitives already
  cover.
