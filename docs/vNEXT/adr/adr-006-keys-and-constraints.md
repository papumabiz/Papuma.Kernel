# ADR-006: Keys and constraints via metamodel and expression indexes

## Status

Accepted (2026-06-11)

## Context

The classic document-store cycle: "JSON is flexible → where are my constraints? →
we rebuild relational structures." Without a plan this happens ad hoc and
inconsistently. Real requirements that cannot be argued away:

- **Uniqueness** (e.g. email unique per tenant),
- **Lookup queries** on single fields (login by email, search by customer number),
- **References between aggregates** (Order → Customer).

## Decision

1. Keys are **declared in the metamodel** (attribute or fluent, cf. the ADR-007
   pattern):

   ```csharp
   builder.For<User>()
       .UniqueKey(x => x.Email)
       .LookupKey(x => x.CustomerNumber);
   ```

2. From this, the kernel materializes **partial expression indexes** on the
   document table:

   ```sql
   CREATE UNIQUE INDEX ux_user_email
       ON papuma.document (tenant_id, (data ->> 'email'))
       WHERE document_type = 'User';
   ```

   Unique violations are surfaced to the caller as a typed
   `UniqueKeyViolationException` (with the key name).
3. **Lookup API** along the declared keys — deliberately no LINQ provider:

   ```csharp
   User? user = await session.LoadByKeyAsync<User>(x => x.Email, "harry@example.com");
   ```

   Everything beyond that (ad-hoc queries, reporting) belongs in projections
   (ADR-009).
4. **References between aggregates** are domain ids without foreign-key
   enforcement. Referential consistency is the application's business or that of
   change handlers (e.g. a handler reacting to a `Customer` delete). The kernel
   offers no cascades for this — deliberately.
5. Index DDL is part of the kernel's schema management (idempotent
   `EnsureSchemaAsync` at startup or explicit CLI/setup), not manual sprawl.

## Consequences

- Constraints exist exactly where they are declared — discoverable, versioned,
  consistently named.
- No creeping return to the relational world: what is not a key gets no index on
  the write store; reporting needs move into projections.
- Partial indexes per document type keep the index set small and precise.
- Cross-aggregate integrity is weaker than with foreign keys — an accepted price;
  where hard consistency is needed, that is a hint the aggregate boundary is in
  the wrong place.
