# Papuma vNEXT — Concepts Explained

Status: living document (started 2026-06-11) ·
[Deutsche Fassung (eingefroren, 2026-06-12)](concepts.de.md)

The ADRs record *decisions* — this document explains the *mechanisms behind them*,
with the examples and lines of thought from the design and implementation phase.
It is deliberately narrative and serves as raw material for tutorials and
onboarding. Every section links its ADR.

---

## 1. Why a single UPDATE closes the race window

→ [ADR-003](adr/adr-003-write-path-concurrency.md)

The naive way to compute a diff is: load the old document, compare, write. But
between loading and writing lies a window of time — if another writer changes the
document right then, the diff is computed against a state that was never actually
replaced. The diff *lies*.

PostgreSQL 18 solves this with a single statement:

```sql
UPDATE papuma.document
SET data = @data, version = version + 1
WHERE ... AND version = @expectedVersion
RETURNING old.data, new.data, new.version;
```

`old.data` is guaranteed to be exactly the state that was replaced — not "the state
from a few milliseconds ago". There is no window, because reading and writing are
the same atomic operation. That is why PG ≥ 18 is a hard requirement (ADR-001) and
not an optimization detail.

Bonus: `new.data` comes back in Postgres's *normalized* jsonb form. The diff is
computed against what is actually stored — client serialization quirks (key order,
number formatting) cannot distort it.

---

## 2. The slow and the fast writer (the seq visibility gap)

→ [ADR-010](adr/adr-010-feed-consumption.md)

The most insidious problem of a polled feed: **sequence numbers are assigned at
INSERT time, but rows become visible at COMMIT time — and those two orders can
cross.**

Concretely: transaction A (the *slow writer*) starts first and draws `seq = 100`.
Transaction B (the *fast writer*) starts later, draws `seq = 101` — and commits
**first**. A naive poller (`WHERE seq > checkpoint`) now sees 101, processes it,
and advances its checkpoint to 101. When A commits later, its 100 lies *behind*
the checkpoint — **it is never processed. Silently lost.**

The solution: every ChangeRecord stores its transaction id (`txid xid8`), and the
poller only reads changes whose transaction lies *before the horizon of all still
running transactions*:

```sql
WHERE seq > @checkpoint
  AND txid < pg_snapshot_xmin(pg_current_snapshot())
```

`pg_snapshot_xmin` is the oldest still-open transaction. As long as the slow
writer is open, the faster 101 also counts as "not yet stable" and is held back.
Only when A commits (or aborts) does the horizon advance and both are delivered in
seq order. No lag window, no heuristics — MVCC itself is the truth.

The price: a very long open *write* transaction stalls feed progress for *all*
consumers. That is accepted and observable (lag metric) — and one more reason why
sessions should be short transactions.

**Who pays for what?** The writers pay **nothing** — the fast writer never waits
for the slow one; both commit independently at full throughput; there is no queue
and no lock between them. The cost is paid exclusively in *consumer latency*,
bounded by the longest concurrently open **write** transaction. With many users
running many short sessions, the horizon advances continuously (more volume tends
to make it better, not worse); read-only sessions don't hold it back at all,
because Postgres assigns transaction ids only on the first write. The one risk
case remains the single long-open write session (e.g. spanning user think time) —
which is exactly what the lag health check is for.

---

## 3. NOTIFY is the alarm clock, polling is the truth

→ [ADR-010](adr/adr-010-feed-consumption.md)

LISTEN/NOTIFY alone would be unsuitable as a delivery mechanism: notifications are
not persistent, and connection drops lose them silently. Polling alone would be
sluggish (latency = poll interval) or expensive (constant fire).

The combination takes the best of both: the worker polls — but instead of sleeping
blindly between cycles, it waits for `NOTIFY papuma_changes` *or* the timeout. The
notification is sent in `CommitAsync` and delivered by Postgres atomically with
the commit — so it can never point at data that does not exist yet. A missed
notification costs at most one poll interval of latency, **never data**.

In the test: the poll interval is deliberately set to 30 seconds — the change
still arrives within milliseconds.

---

## 4. Stop-the-line: why order beats progress

→ [ADR-009](adr/adr-009-projections-as-dumb-handlers.md)

When a handler throws at seq 105, there are two schools: *keep running and catch
up on 105 later* (maximum throughput) or *stop until 105 is resolved* (strict
order). vNEXT stops — the checkpoint stays before 105, retry with exponential
backoff.

Why? Because handlers should be *allowed to rely on* the ordering: a SQL
projection that needs `Insert(105)` before `Update(106)` of the same document
could otherwise never be written naively. Out-of-order catch-up shifts the
complexity into every single handler — the exact opposite of "projections are
dumb".

So that a permanently broken change does not block the line forever, there is the
**poison valve**: after `MaxAttempts` the change is skipped — but the failure
entry remains as a permanent alarm record in `papuma.failure` (an operations
topic, not data loss in the feed: the change itself is still there and can be
caught up via rebuild after a fix).

**Who pays for what?** Only the line of the *one* failing handler stops — writers
and other handlers (own checkpoints) are unaffected. The cost is latency, not
throughput: during the backoff, that handler's lag grows by `write rate × backoff
duration`; afterwards it catches up in batches, which is much faster than
real-time consumption. The prerequisite is catch-up headroom — a handler that
permanently cannot keep up with the write rate has growing lag with or without
stop-the-line (that is limit 2 from §14, not the waiting mechanism).

**Where writers really do interact** (for completeness): only at the *hot
document*. Two concurrent writes to the same row are serialized by Postgres at
the row lock for the (short) duration of the first transaction, then the version
check kicks in → `ConcurrencyException` → app retry. Different documents do not
interact at all: 10,000 users on 10,000 documents scale linearly; 10,000 users on
*one* global counter serialize at the row — a modeling topic (shard the counter),
not an engine problem.

---

## 5. Savepoints: why one failure does not destroy the session

→ Architecture §5, [ADR-003](adr/adr-003-write-path-concurrency.md)

Session = one transaction (unit of work) has a problem: in PostgreSQL, "once an
error, always an error" — after a constraint violation the *entire* transaction is
aborted, and the three successful writes before it would be lost too.

That is why every write runs under a **savepoint**:

```text
SAVEPOINT papuma_write
  → execute the write
  → on success:  RELEASE
  → on failure:  ROLLBACK TO SAVEPOINT  (and rethrow the exception)
```

A failed write (concurrency conflict, unique violation, rejected validator) rolls
back only *itself* — earlier writes stay intact, the session remains usable, and
the caller can react (retry, different value, abort). This reconciles
unit-of-work atomicity with typed failure cases as *normal* program flow.

A side effect for the guards: when the schema guard throws *after* the UPDATE has
already been applied (it needs `old.schema_version` from the RETURNING), the
savepoint rollback undoes the UPDATE. Check-after-write is safe here because
writing means nothing until commit.

---

## 6. Null ≠ absent: the detail that makes diffs reversible

→ [ADR-004](adr/adr-004-changerecord-diff-only.md)

`{"email": null}` and a document *without* an `email` field are different states
in JSON. A diff format that encodes both the same way cannot be applied backwards
— should `ApplyReverse` *remove* the field or *set it to null*?

The solution costs nothing: existence is encoded via the **presence of the keys**,
while the value may legitimately be `null`.

```json
{ "email": { "new": "x@y.z" } }              ← field did not exist before
{ "email": { "old": null, "new": "x@y.z" } } ← field existed and was null
```

Only this distinction makes the two invariants provable that the diff engine
guarantees via property tests: `Apply(before, diff) == after` and
`ApplyReverse(after, diff) == before`.

---

## 7. Why arrays are diffed atomically

→ [ADR-004](adr/adr-004-changerecord-diff-only.md)

Index-based array diffs (`roles[2]: {old, new}`) look precise but are a trap: when
an element is inserted at the front, all indices behind it "change" — the diff
becomes huge and semantically misleading ("roles[5] changed", although something
was merely inserted). Identity-based diffs (LCS, move detection) solve that, but
cost exactly the complexity that makes Apply/Reverse error-prone.

vNEXT chooses the boring, provably correct variant: **if arrays differ, there is
one entry with the full old and new array.** Reversible without special cases,
projection-friendly ("roles changed"), and policies apply to exactly one path.
Element granularity remains a later optimization *behind* the same wire format.

The same logic applies to a **type change on a path**: if the object `address`
becomes a string, there is no recursion — an atomic entry with the whole object as
`old` is produced instead. Recursing across a type change would break
reversibility.

---

## 8. Why rollback across redacted fields *must* fail

→ [ADR-007](adr/adr-007-privacy-policies.md), [ADR-008](adr/adr-008-rollback-is-update.md)

`RollbackAsync` reconstructs old states by applying diffs backwards. A redacted
entry (`{"changed": true}`) contains *no value*, though — that is its purpose.
The kernel could guess (leave the old value empty? keep the current one?), but
every variant would be **silently wrong state**.

Hence: a typed error (`RollbackNotPossibleException` with path and policy kind)
instead of gut feeling. Whoever wants to restore the field does it explicitly via
the reference source — auditable instead of magical.

A consequence that only became visible during implementation: **insert and delete
diffs** of sensitive fields are redacted too (otherwise the last value would sit
in plain text in the feed). A rollback across a delete/recreate chain of a
document with sensitive fields therefore fails as well — correctly, because the
kernel simply does not know the old secrets.

---

## 9. Insert after delete: why versions keep counting

→ [ADR-003](adr/adr-003-write-path-concurrency.md)

If a document is deleted and its id is reused later, a naive insert would start at
version 1 again — and collide with the change history: records already exist there
for version 1 (insert) and 2 (delete), and the unique index
`(scope, tenant, type, id, version)` would fire.

That is why the insert continues at `max(change.version) + 1`: insert → delete →
insert yields versions 1, 2, **3**. The history per document id stays gapless and
chronologically readable — including the rebirth. And exactly this unbroken chain
is what lets `RollbackAsync` work even across delete/recreate: insert diff
backwards = empty object, delete diff backwards = the old state.

`expectedVersion: 0` keeps its semantics of "I expect the document not to exist" —
the caller does not need to know anything about the prior history.

---

## 10. Why patch needs no load — and when LWW is right

→ [ADR-012](adr/adr-012-partial-updates.md)

A patch does not load the document — neither the caller nor the kernel internally.
The "read" happens inside the UPDATE itself: `jsonb_set` applies the change to the
state *in Postgres*, and `RETURNING old/new` delivers the diff material. One
roundtrip, no window.

This allows a differentiated concurrency posture:

- **Without `expectedVersion`** a patch is deliberate *field-level
  last-writer-wins*. Two admins changing `displayName` and `phone` at the same
  time have no real conflict — the version number still serializes their patches
  cleanly (2, 3), and each change's diff is correct.
- **With `expectedVersion`** the patch becomes read-modify-write — necessary as
  soon as the new value depends on previously *read* state.
- **`Increment`** needs neither: the dependency on the current value is resolved
  as a SQL expression inside the statement — atomic by construction.

The full `Save`, by contrast, *always* demands `expectedVersion`: there, the whole
document is the asserted state, and an unchecked overwrite would be exactly the
lost-update problem from the multi-user discussion (ADR-003).

---

## 11. Leader coordination without a consensus protocol

→ [ADR-010](adr/adr-010-feed-consumption.md)

When several processes run the same handler (scale-out, deployment overlap),
exactly one active processor per handler is needed — but no ZooKeeper, no lease
protocol. The checkpoint *row* itself is the lock:

```sql
SELECT last_seq FROM papuma.checkpoint
WHERE handler_name = @name
FOR UPDATE SKIP LOCKED
```

If another process holds the row, `SKIP LOCKED` returns immediately with an empty
result — the process skips the handler for this cycle without blocking. If the
holder dies, Postgres releases the lock together with its transaction
automatically. Failover is thus a side effect of transaction semantics, not a
separate system.

---

## 12. Why policies act differently on event payloads than on diffs

→ [ADR-013](adr/adr-013-business-event-log.md), [ADR-007](adr/adr-007-privacy-policies.md)

In the change feed, policies transform *diff entries* — `{"changed": true}`
instead of values. With events that does not work: the payload is the fact itself,
and consumers want to *deserialize* it as `UserLoggedIn`. A `{"changed": true}` in
the middle of the payload would destroy its shape.

So event payloads keep their natural form, and policies act on the fields
themselves:

- **Redact / DoNotTrack** → the field is *removed* before storing (deserialization
  yields the default — consumers never see protected values)
- **Hash** → the value is replaced by the SHA-256 hex string (comparable without
  content)
- **Reference** → **rejected at model build.** In a diff, a reference points at
  the value inside the document ("the truth lives elsewhere"). But an event *is*
  itself the record — there is no place the reference could point to. Better a
  loud error at startup than a reference into the void.

---

## 13. Two feeds, no global order — and why that is enough

→ [ADR-013](adr/adr-013-business-event-log.md)

Change feed and event log have separate sequences and separate checkpoint spaces
(event handlers are tracked internally with an `event:` prefix in the same
checkpoint table). A handler consuming both gets **no guaranteed order between the
feeds** — deliberately: a unified sequence (v1's `kind` column in a unified feed)
would have chained both worlds together.

What establishes the connection instead is the session's **`correlationId`**: the
login fact (`Append(UserLoggedIn)`) and the state patch (`lastLoginAt`) commit
atomically in one transaction and carry the same correlation — whoever needs
relationships correlates instead of ordering. And because both tables use the
same `txid` mechanics, the gap guarantee from section 2 holds in both feeds.

---

## 14. The scaling model of the feed engines — limits and escape routes

→ [ADR-009](adr/adr-009-projections-as-dumb-handlers.md), [ADR-010](adr/adr-010-feed-consumption.md)

Per process there is **one** `ChangeFeedProcessor` and **one** `EventFeedProcessor`;
what gets registered are *handlers*, not processors. Parallelism arises across app
instances — and there, `FOR UPDATE SKIP LOCKED` makes scale-out **failover, not
throughput**. Per handler, exactly one instance consumes at any time, because
strict seq ordering demands exactly one consumer (like Kafka with one partition).

The three real limits, in the order you hit them:

1. **The slowest handler determines cycle latency.** Handlers run sequentially per
   cycle; they are data-decoupled (own checkpoints) but latency-coupled. Up to
   ~10–20 brisk handlers this is irrelevant; one handler with an external HTTP
   call drags everyone into its latency.
2. **Throughput per handler is single-threaded** — the architectural ceiling. A
   projecting handler (1 SQL write per change) realistically manages a few hundred
   changes/s. If the application writes faster permanently, lag grows without
   bound; more instances do not help.
3. **Read amplification**: every handler reads the full feed (no type filter in
   SQL) — cheap thanks to a PK range scan from the checkpoint, but measurable at
   volume × handler count.

**Not a problem**: NOTIFY storms (the processor drains until "empty" anyway),
connections (1–2 + LISTEN per processor), the two processors side by side
(separate tables and checkpoint spaces).

**Design stance**: correctness + observability before throughput. Being
pull-based, there is no backpressure collapse — only growing lag, and exactly
that is made visible by `GetLagAsync` + health check, long before anything tips
over.

**The planned escape routes** (deliberately deferred until lag metrics show the
need): handler parallelization within the cycle (`Task.WhenAll`, solves limit 1 —
small, since every handler has its own connection/checkpoint), a SQL-side
`document_type` filter per handler (solves limit 3), and as a real feature handler
sharding by `document_id` hash (solves limit 2 while preserving the
domain-relevant ordering *per document* across N parallel consumers).

---

## 15. Observability without a vendor: why BCL primitives suffice — and the link trick

→ Phase 11, [observability.md](observability.md)

In .NET, "OpenTelemetry or something better?" is a false dichotomy: `Meter` and
`ActivitySource` from the BCL *are* the vendor-neutral sources, and OTel,
Prometheus or `dotnet-counters` are interchangeable consumers. The kernel
therefore takes zero dependencies and instruments directly —
`AddMeter("Papuma.Kernel")` is all the application needs.

Two details that are not obvious:

**The lag gauge is a cache, not a live query.** Observable gauges are queried
synchronously, but the lag requires a DB query. So `GetLagAsync` feeds a cache
that the run loop refreshes in its idle moments — gauge freshness ≈ poll
interval. And because observable gauges cannot be deregistered individually, each
processor holds its *own* meter (same name!), disposed together with it.

**Handler spans link instead of inheriting.** The session writes the active
`traceparent` into the change metadata; the handler span takes it as a span
**link**, not as a parent. Deliberately: a parent would claim that feed processing
is part of the request latency — it is not; it is asynchronous batch work,
possibly minutes later (backoff!). The link correctly says "was caused by", and
the trace viewer still answers with one click which request triggered a
projection. Privacy bonus: thanks to policy application on the diffs, the entire
observability pipeline is low-PII by design.

---

## 16. Reading with guarantees: session loads, SQL views and projections

→ [ADR-002](adr/adr-002-document-as-truth.md), [ADR-005](adr/adr-005-schema-evolution.md), [ADR-006](adr/adr-006-keys-and-constraints.md)

Projections are asynchronous — but **the document store is the truth**, and
`LoadAsync`/`LoadByKeyAsync` read it directly and transactionally consistent. The
classic "a password change must be readable immediately" is therefore the built-in
normal case: a login check via `LoadByKeyAsync` reads the state *now*, without
feed, without lag, index-backed via the declared key.

**SQL views over the JSONB store** are legitimate as an additional "read lens" —
that is exactly why the truth lives as JSONB *in Postgres*. A view is guaranteed
current (same MVCC snapshot), needs no sync machinery, and violates no ADR. Four
caveats come with it:

1. **Read only.** Writes always go through the session (diffs, policies,
   concurrency).
2. **`security_invoker = on`** (PG ≥ 15) is mandatory — otherwise Postgres
   evaluates the RLS policies against the view owner instead of the caller, and
   tenant isolation is silently bypassed.
3. **Views see the stored shape, not the upcast one.** The upcaster pipeline runs
   in the kernel, not in SQL — after a rename, old documents still lie around in
   the old shape thanks to lazy upcasting (`COALESCE(data->>'new', data->>'old')`
   as a transition, or restrict views to schema-stable fields).
4. **Policies do not apply** — the store contains plain text; keep grants on views
   that expose sensitive fields correspondingly tight.

The decision matrix:

| Need | Tool | Consistency |
|---|---|---|
| Strong-consistency read in code (login, business logic) | `LoadAsync` / `LoadByKeyAsync` | immediate |
| Ad-hoc SQL, reporting, BI on current data | View (with the 4 caveats) | immediate |
| Heavy read models, aggregations, external targets | Projection via handler | eventual (lag observable) |

Materialized views are the worst of both worlds: they bring staleness back
(`REFRESH` cycle) without offering the freedom of a real projection.

---

## 17. The bounded counter: inventory without overselling

→ [ADR-012](adr/adr-012-partial-updates.md), §4 (hot document), §10 (Increment)

The shop problem "only N items in stock, never oversell" has two correct
solutions — and one clear recommendation:

**Optimistic (load + check + save with `expectedVersion`)** can never oversell:
the version check makes read-check-write effectively atomic; the loser gets the
`ConcurrencyException` and tries again. Under flash-sale load, however, this
becomes a retry carousel at the hot document — correct, but wasteful.

**The atomic conditional decrement** composes two existing primitives:

```csharp
m.Document<Inventory>(d => d.Validate(inv =>
{
    if (inv.Stock < 0) throw new OutOfStockException(inv.Id);
}));

await session.PatchAsync<Inventory>(skuId, p => p.Increment(x => x.Stock, -1));
// throws OutOfStockException if the stock would go negative — nothing written
```

`Increment` computes on the current value inside the statement (no conflict
window, no `expectedVersion`), competing buyers are briefly serialized by Postgres
at the row lock, and the validator checks the *stored result* before commit — if
the stock would go negative, the savepoint rolls the UPDATE back. Every buyer up
to stock 0 gets through without a single retry; after that, rejection is typed.
ADR-012 rejected "conditional patches" as a catalog feature — this composition is
the sanctioned route to conditional write semantics.

Modeling: stock as its **own small document** per SKU (decouples content
maintenance from stock movements — field-level LWW or not, the histories stay
cleanly separated), cancellation as an `Increment(+1)` compensation. Free bonus:
the change feed of the inventory document is a gapless **stock ledger**
(`stock: {old: 5, new: 4}` with the `correlationId` of the order). For extreme
cases (tens of thousands of buyers on *one* SKU), the row serialization itself
becomes the ceiling → shard the stock into buckets (a modeling topic, §14).

---

## 18. Human-in-the-loop and the workflow question: waiting is state, not a thread

→ §4 (stop-the-line), [ADR-003](adr/adr-003-write-path-concurrency.md) (expectedVersion),
[ADR-013](adr/adr-013-business-event-log.md) (events)

Two seemingly different questions — "can a human participate in the feed?" and
"can I build a workflow engine on top of this?" — have the same answer, because
they are the same pattern.

**The hard rule first: a feed handler never waits for a human.** Stop-the-line
(§4) means: as long as a handler does not return, its checkpoint does not advance
— a blocking handler halts *its entire feed* and ends up as a poison entry after
the backoff. Humans answer in hours or days; no thread, no process, no deployment
survives that reliably.

The right mechanism turns the waiting around: **the handler materializes the
question as a document and is done.**

```csharp
// Handler on OrderPlaced: does the order need an approval?
public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
{
    await using var session = _store.OpenSession(change.Scope);
    // Deterministic id: the same delivery creates the same task (idempotency!)
    var taskId = $"approval-{change.DocumentId}-v{change.Version}";
    await session.SaveAsync(new ApprovalTask(taskId, change.DocumentId,
        Status: ApprovalStatus.Pending, RequestedAt: _clock.UtcNow), expectedVersion: 0);
    await session.CommitAsync();
}
```

The checkpoint advances, the feed keeps running. The waiting now lives **in the
store as persisted state** — crash-safe, deployable, arbitrarily long. The human
decision is then a perfectly normal write:

```csharp
await session.PatchAsync<ApprovalTask>(taskId, p => p
    .Set(x => x.Status, ApprovalStatus.Approved)
    .Set(x => x.DecidedBy, actorId),
    expectedVersion: 1); // two approvers at once → one loses, typed
```

And this write itself produces a change that the next handler reacts to. The
"loop" through the human is not a blocked call but a chain: *change → task
document → human write → change → next step.* Every link is atomic, versioned and
auditable.

Two traps the pattern defuses:

1. **At-least-once**: if the process crashes between task creation and checkpoint
   progress, the change is delivered again. Deterministic task ids (derived from
   the triggering document id + version) turn the repetition into a harmless
   conflict instead of a duplicate.
2. **Competing deciders**: `expectedVersion` on the task serializes the decision —
   the second approver gets the `ConcurrencyException` and sees "already decided
   by X" in the UI (via `GetHistoryAsync`).

**The workflow engine is this pattern, generalized.** The classic saga / process
manager model needs four things, and all four are kernel primitives:

| Workflow need | Kernel primitive |
|---|---|
| Durable state machine per instance | Workflow document (`currentStep`, data, correlation) |
| React to facts | Change and event feed handlers |
| Protect transitions against races | `expectedVersion` when writing the instance |
| Complete execution log | The instance's change feed — every transition with actor, time, correlation, reversible |

A handler loads the instance, decides the transition (a pure function: state +
trigger → new state), writes with `expectedVersion`. If two triggers fire at
once, one loses cleanly and evaluates the *new* state on retry — exactly the
semantics you want for state machines. Compensation ("saga rollback") is business
events plus handlers that clean up backwards. Human tasks are the section above.

**The only missing primitive is timers** ("escalate after 48 h without an
answer"). Deliberately: a scheduler is its own responsibility with its own
guarantees. The recipe is small — `dueAt` as a field on the instance (queryable
via key mapping or a view) plus a hosted service that periodically polls due
instances and appends a `WorkflowTimerFired` event; from there the normal handler
chain takes over. Leader coordination for this poller already has its template in
`FOR UPDATE SKIP LOCKED` (§11).

What the kernel will *not* become: a BPMN engine with DSL and designer. The kernel
provides durable state, reactive feeds, atomic transitions and the audit log — the
workflow *definition* (which steps, which rules) is application code, or a later
separate package on top. This boundary is the same as with GDPR (ADR-015):
mechanisms below, decisions above.

---

## 19. Checkpoints, backup and rebuild: what is truth, what is derivable?

→ [ADR-009](adr/adr-009-projections-as-dumb-handlers.md) (checkpoints),
[ADR-013](adr/adr-013-business-event-log.md) (retention), §16 (projections)

**How does a processor know where it was after a restart?** From
`papuma.checkpoint`: one row per handler (`handler_name → last_seq`), in the same
database as the feed itself. After every successfully processed record, the
processor advances `last_seq`; at startup it reads the row and continues exactly
there — whether the process shut down cleanly, crashed, or moved to another
machine. There is no in-memory state that could get lost: the position *is* a
database row.

From this follows the **at-least-once** guarantee: the checkpoint only advances
after the handler succeeded. If the process crashes in between, the record is
delivered again — hence the idempotency obligation for handlers (and the
deterministic-id pattern from §18). The alternative — checkpoint *before* the
handler — would be at-most-once: no duplicate, but silent gaps in the projection.
For derived state, "duplicated but idempotent" is the only right choice.

**When do you rebuild a projection?** Four typical situations:

1. **A bug in the handler logic** — the projection is *computed* wrong. Deploy the
   fix, `ResetCheckpointAsync(handlerName)`, replay from seq 0.
2. **A new projection** — a freshly registered handler starts at seq 0. The
   "rebuild" is thus not a special mode but the normal case of the first start:
   every projection comes into being as a replay of the entire history.
3. **A read-model schema change** — the new column needs historical values that
   only exist in the feed.
4. **The projection target is lost** — Elasticsearch index deleted, cache flushed,
   external database restored.

The hard boundary: reset only **projections** (idempotent, derivable) — never
effect handlers. A reset email handler re-sends the entire mail history. The
distinction "projection vs. effect" is a design decision per handler, made when
writing it, not when resetting.

**What belongs in the backup?** Logically only the truth: `papuma.document` (the
state), `papuma.change` (the complete history) and `papuma.event` (the facts) —
plus the trivial infrastructure tables `checkpoint`/`failure`. Projections are by
definition derivable. But two qualifications make the pure doctrine practical:

1. **Events with retention are the exception to derivability.** Changes are never
   deleted — the version history is complete, every document projection remains
   reconstructible forever (modulo redaction markers, §8). Purged events, however,
   are *gone* (ADR-013: a fact store, not a version store). A projection over
   events with retention is **not** reconstructible from the log: either back up
   its state, or choose the retention much longer than any conceivable rebuild
   need.
2. **Rebuilds cost time.** Backing up projections is not a correctness decision
   but a recovery-time one: restore + full replay of years of changes can take
   hours during which read models are missing. If the projections live in the same
   Postgres instance, the question is moot anyway — `pg_dump`/PITR back them up as
   a consistent snapshot, *including the exactly matching checkpoints* (that is
   the quiet advantage of checkpoints living in the same database: snapshot
   consistency for free).

For **external targets** (Elasticsearch, Redis, third-party systems), the simple
rule after a restore: do not hope that external state and the restored checkpoint
match each other — reset the checkpoint and rebuild. At-least-once plus
idempotency make exactly that safe.

---

## 20. Snapshots: the concept exists — inverted

→ [ADR-002](adr/adr-002-document-as-truth.md) (document = truth),
§6 (reversible diffs), §19 (rebuild)

Event-sourcing systems know **snapshots**: periodically persisted intermediate
states, so that loading an aggregate does not have to replay the whole event
stream. The question "does that exist here too?" has a neat answer: yes — but
inverted. **`papuma.document` *is* the snapshot.**

In classic event sourcing, the events are the truth and the state is derived; the
snapshot is a cache maintained by a background process, and it can go stale.
Document-sourced CQRS turns the relationship around: the state is the truth, the
feed is derived (ADR-002). The "snapshot" is thus written in **the same
transaction** as every change — by construction it can neither go stale nor lag
behind, and the problem snapshots solve does not exist at its main site. In
detail, at the three places where classic systems need snapshots:

1. **Loading an aggregate**: `LoadAsync` is a single row read. No replay, ever —
   regardless of whether the document has 3 or 30,000 versions.
2. **Time travel** ("the document at version 12"): because diffs are reversible
   (§6), historical states are reconstructed **backwards from the current
   document** rather than forwards from version 0. The nearest snapshot is always
   the head: version 498 of 500 costs two reverse applies instead of 498 forward
   applies — and the closer the requested version is to the present (the common
   case: conflict UIs, "what just changed?"), the cheaper it gets. Caveat:
   redacted fields block the journey backwards (§8) — intended, otherwise
   policies would be worthless.
3. **Projection rebuild**: the only place where "replay the whole feed" really
   exists (§19). But projections are persistent and checkpointed — they do *not*
   rebuild at startup, only on an explicit reset. And the reset almost always
   happens because the *handler logic* changed — at exactly that moment a
   projection snapshot would be **invalid anyway**, computed with the old logic.
   That is the classic snapshot trap in event sourcing (snapshot invalidation on
   logic changes is easily forgotten); here it does not arise, because the
   reset-to-0 case is the only one left and a snapshot could contribute nothing
   valid there.

Should you ever need an intermediate state for a *very* expensive projection with
*stable* logic, it is trivial: the projection persists its own state — it does
that anyway as a read model — and its checkpoint is the matching position.
"Projection + checkpoint" *is* the snapshot pair; there is nothing additional to
invent.

**The backup side question**: in classic systems, snapshots are derived and thus
optional in backups (rebuildable from the events — the same recovery-time
trade-off as in §19). Here, `papuma.document` is the truth itself and therefore
the core of every backup; the question dissolves.

---

## 21. Polyglot consumers: the feed as a cross-language API

→ [ADR-004](adr/adr-004-changerecord-diff-only.md) (wire format),
[ADR-011](adr/adr-011-no-business-events-in-storage.md) (the translator edge),
§2 (gapless reads), §16 (views)

Can an application written in another language react to changes and events from
a Papuma-based system? Yes — and by design. The feed is deliberately *not* a
.NET-private artifact: it is two ordinary Postgres tables with a documented,
stable wire format (the ADR-004 diffs are flat JSONB, queryable even from SQL:
`diff ? 'email'`). Everything the .NET processor does is plain SQL — read the
checkpoint row, read gaplessly, advance the checkpoint, wait on NOTIFY. There
are two consumption paths, with a clear decision rule.

**Path A: direct SQL from the foreign language.** A Python/Go/Node consumer
replicates the poll loop in ~50 lines and may even keep its position in the
same `papuma.checkpoint` table (`handler_name` is just text — pick a unique
one). Three things it must take seriously:

1. **The gapless predicate is mandatory, not an optimization**:

   ```sql
   SELECT ... FROM papuma.change
   WHERE seq > @checkpoint
     AND txid < pg_snapshot_xmin(pg_current_snapshot())
   ORDER BY seq
   ```

   A naive `seq > checkpoint` poll walks into the slow-writer trap (§2) and
   silently loses changes.
2. **At-least-once discipline**: process, then advance the checkpoint — and be
   idempotent, exactly like a .NET handler (§19).
3. **It sees the stored shape**: upcasting runs in the kernel, not in SQL — old
   documents and diffs carry their historical `schema_version` (the additive
   rules of ADR-005 are what keep this manageable). And RLS applies: set the
   scope GUCs per transaction, or use the `'All'` scope mechanism for
   cross-tenant workers.

The built-in advantage that makes path A safe at all: **policies already
minimized the feed at write time** (ADR-007). A foreign-language reader cannot
reach sensitive values — the feed is safe reading material in every language.
What path A does *not* get is the engine's machinery: retry with backoff,
poison handling, leader coordination, lag metrics. A simple consumer can live
without them; a critical one has to rebuild them — which is the cue for path B.

**Path B: bridge through a .NET handler.** A dumb `IChangeHandler` publishes to
a language-neutral transport — webhook, Kafka/RabbitMQ, Redis stream, an SSE
endpoint. This is exactly the integration edge from ADR-011 (the event
translator), and ADR-005 already notes that Protobuf is a fine choice *there*.
The benefit: ordering, checkpoints, retry and poison handling stay in one place
(the engine), and the foreign application gets a contract in its own world
instead of access to your database.

**The decision rule:** same Postgres instance reachable + simple consumption
(a projection, a sync, analytics) → path A is legitimate and even elegant — the
feed *is* the API; that is precisely why the truth lives as JSONB in Postgres.
You need decoupling, transformation, delivery guarantees across system
boundaries, or the database must not be shared → path B. (And for plain *state
reads* from other languages, the SQL views of §16 exist anyway.)

One boundary stays language-independent: foreign consumers are consumers.
**Writing** goes through the kernel's session — only there do diff, policies,
version chain and NOTIFY arise. A foreign app that needs to write talks to the
Papuma application's API, not to `papuma.document`.

---

## 22. Event buses (NATS, Kafka, RabbitMQ): the feed is the outbox

→ [ADR-002](adr/adr-002-document-as-truth.md) (derived feed),
[ADR-009](adr/adr-009-projections-as-dumb-handlers.md) (handlers),
§21 (polyglot consumers, path B)

Where does an event bus fit next to the kernel? Behind the feed — never beside
it. The reasoning has a neat punchline: **the derived change feed already *is* a
transactional outbox.**

Publishing to a bus directly from the write path would be the classic dual-write
problem: there is always a window in which one of the two writes succeeds and
the other does not — exactly what the outbox pattern was invented to fix
(laboriously, in v1). In Papuma the ChangeRecord is created *in the same
transaction* as the document (ADR-003): atomic, gapless, replayable. Nothing can
be forgotten, nothing can be published that was never committed. The bus
attachment is therefore an ordinary dumb handler — and inherits ordering,
checkpoints, retry and poison handling from the engine:

```csharp
public sealed class NatsChangePublisher(INatsJSContext jetStream) : IChangeHandler
{
    public string Name => "nats-publisher";

    public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
    {
        var subject = $"papuma.change.{change.Scope.TenantId}.{change.DocumentType}";
        await jetStream.PublishAsync(subject, Serialize(change),
            opts: new NatsJSPubOpts { MsgId = $"change-{change.Seq}" },
            cancellationToken: ct);
    }
}
```

Three details make NATS a particularly good fit:

1. **JetStream dedup via `Nats-Msg-Id`**: using the feed `seq` as the message id
   turns the handler's at-least-once into effective exactly-once toward the bus
   — the idempotency obligation dissolves into one line. (Kafka achieves the
   same with an idempotent producer + seq-keyed messages.)
2. **The subject hierarchy mirrors the scope model**:
   `papuma.change.{tenant}.{type}` means NATS accounts/permissions can *extend*
   tenant isolation onto the transport — a consumer allowed to subscribe only to
   `papuma.change.acme.>` structurally never sees foreign tenants.
3. **Policies already acted**: what reaches the bus is the policy-applied record
   (ADR-007) — the bus cannot distribute sensitive values.

**Core NATS vs. JetStream** is the second fork. Core (at-most-once, ephemeral)
fits throwaway signals — UI pushes, cache invalidation: a lost signal costs
nothing because the truth sits in Postgres and is reloaded on demand. JetStream
(persistent, at-least-once, consumer groups) fits real integrations. But even
with JetStream: **the stream on the bus is a copy; the feed remains the truth
and the replay source.** A rebuild means resetting a checkpoint at the feed,
never "rewinding the bus". Retention on the bus is purely a transport decision
and never endangers data.

**When is a bus worth it?** Many (polyglot) consumers, fan-out across network
boundaries, decoupling consumers from the database → bus (this is the
industrial-strength version of §21 path B). A single .NET application reacting
to its own changes → handlers suffice; a bus would be pure operational
complexity. And the kernel itself never needs one internally — LISTEN/NOTIFY as
the wakeup is deliberately dependency-free (ADR-010).

Often the right granularity for the bus is not the raw change but the
**translated domain event** (ADR-011): the same handler that detects
`status: Pending → Paid` publishes `OrderPaid` to the bus — external consumers
get a stable, intention-revealing contract instead of your document shapes.

---

*Maintenance note: add new explainers from later phases here — this document is
the collection point for the "why behind the how" and raw material for the
tutorials (phase 9).*
