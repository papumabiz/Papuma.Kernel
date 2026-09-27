# Recipe: Stream-shaped aggregates — ledgers without event sourcing

Background: [ADR-023](../adr/adr-023-no-event-sourcing-mode.md) (no ES mode),
[ADR-013](../adr/adr-013-business-event-log.md) (the event log),
[ADR-003](../adr/adr-003-write-path-concurrency.md) (optimistic concurrency).
Verified by `tests/Papuma.Kernel.Tests/Events/StreamShapedAggregateTests.cs`,
which runs the code below against PostgreSQL 18.

## The problem

Some aggregates are naturally a sequence of occurrences: an account and its
postings, a meter and its readings, a loyalty card and its bookings. Two naive
mappings both hurt:

- **Only the document** (`Balance` field): the diff shows `100 → 70`, but not
  *why* — the booking text, the counterparty, the reference are lost. A
  translator handler (ADR-011) cannot derive what was never stored.
- **The postings inside the document** (`List<Entry>`): the document grows
  without bound, every write rewrites the whole list, and every diff carries it.
  Fine for a handful of entries, wrong for a ledger.

Event sourcing answers "the postings are the truth, the balance is a fold".
Papuma answers differently (ADR-023): **the balance is the truth, the postings
are facts** — written together, never one rebuilt from the other.

## The pattern

A Decider split into its two halves (see the
[Event Modeling recipe](event-modeling-slices.md#what-happens-to-the-decider)),
with one difference to ES: `Evolve` runs **once, on write** — never on load.

```csharp
public sealed record Account(string Id, string Owner, decimal Balance = 0m);

// The fact. AccountVersion ties it to the document version it produced;
// BalanceAfter makes each fact self-checking.
public sealed record Posted(
    string AccountId,
    decimal Amount,
    string Reference,
    long AccountVersion,
    decimal BalanceAfter);

public static class Ledger
{
    // decide: pure, testable without a database
    public static Posted Decide(Account account, long version, decimal amount, string reference)
    {
        if (amount == 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A posting must move money.");
        }

        var balanceAfter = account.Balance + amount;
        if (balanceAfter < 0m)
        {
            throw new InvalidOperationException($"Account {account.Id} would be overdrawn.");
        }

        return new Posted(account.Id, amount, reference, version + 1, balanceAfter);
    }

    // evolve: the fold ES would run on every load — here it runs exactly once
    public static Account Evolve(Account account, Posted posted) =>
        account with { Balance = account.Balance + posted.Amount };

    public static async Task<Posted> PostAsync(
        DocumentSession session, string accountId, decimal amount, string reference, CancellationToken ct = default)
    {
        var current = await session.LoadAsync<Account>(accountId, ct)
            ?? throw new DocumentNotFoundException(nameof(Account), accountId);

        var posted = Decide(current.Document, current.Version, amount, reference);

        await session.SaveAsync(Evolve(current.Document, posted), current.Version, ct); // 1. state + concurrency check
        await session.AppendAsync(posted, ct);                                           // 2. the fact
        return posted;
    }
}
```

Registration — the fact is an ordinary event type, so policies (ADR-007) apply
to its payload:

```csharp
o.Model(m => m
    .Document<Account>()
    .Event<Posted>()); // no Retention(...): ledger facts are kept
```

The caller commits; on `ConcurrencyException` it disposes the session and
retries with a fresh one:

```csharp
await using var session = store.OpenSession(scope, options);
await Ledger.PostAsync(session, accountId, -30m, "groceries", ct);
await session.CommitAsync(ct);
```

## Save first, append second — not a style choice

Each session write runs under its own savepoint, so a failed write leaves the
session usable *and earlier writes intact* (architecture §5). With the order
reversed, a conflict on `SaveAsync` does **not** undo the preceding
`AppendAsync` — a caller that catches the exception and commits anyway persists
a fact for a state change that never happened. The test
`AppendBeforeSave_SurvivesTheConflict_WhenTheCallerCommitsAnyway` pins exactly
that behavior.

Saving first makes the optimistic check the gate: the loser of a race fails
before it has appended anything (`ConcurrentPosting_LoserAppendsNothing`), and
the winner's fact and state share one commit and one `correlationId`.

For hot aggregates where conflicts become frequent, the
[bounded counter](../concepts.md) (concepts §17) — `PatchAsync` with
`Increment` plus a validator — avoids the load entirely; read the new balance
from `SaveResult.GetDocument<T>()` and put it into the fact.

## Reconcile, never rebuild

Because every fact carries `Amount`, the facts can be checked against the state
at any time — in a nightly job, a health check, or a test:

```sql
SELECT d.scope, d.tenant_id, d.id,
       (d.data ->> 'balance')::numeric                  AS balance,
       coalesce(sum((e.payload ->> 'amount')::numeric), 0) AS facts
FROM papuma.document d
LEFT JOIN papuma.event e
       ON e.scope = d.scope AND e.tenant_id = d.tenant_id
      AND e.event_type = 'Posted' AND e.payload ->> 'accountId' = d.id
WHERE d.document_type = 'Account'
GROUP BY d.scope, d.tenant_id, d.id, d.data
HAVING (d.data ->> 'balance')::numeric <> coalesce(sum((e.payload ->> 'amount')::numeric), 0);
```

A row in that result is a bug to investigate — not a signal to overwrite the
document from the facts. The document is the truth (ADR-002); if `Evolve` was
wrong, the correction is a documented update (or rollback, ADR-008), which
itself produces a change record.

## Reading the stream

The event log has no stream-id column. For an account statement, project the
facts into an application table with an `IEventHandler` — see
[read models in the same database](same-database-read-models.md) — keyed and
indexed the way the UI queries it. Ad-hoc audits can filter
`payload ->> 'accountId'` directly.

## When this is not enough

This pattern gives up one thing on purpose: **retroactive state**. If you must
be able to change the fold and recompute every past balance from the facts, or a
regulator requires state to *be* a projection of an append-only stream, you are
in event-as-truth territory — use a dedicated ES store for that bounded context
and integrate via the feeds (ADR-023 point 4,
[Event Modeling recipe — the boundary](event-modeling-slices.md#the-boundary--when-to-reach-for-real-event-sourcing-instead)).
