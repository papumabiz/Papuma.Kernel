# Papuma.Kernel — Response to the Security & Architecture Review

**Date:** 2026-06-13
**Responding to:** `SECURITY_ARCHITECTURE_REVIEW.md` (2026-06-12)
**Method:** Every claim re-verified against the current code on `master`. Findings
are classified as **Confirmed** (legitimate, we agree), **Overstated** (real but
mis-severitied or mis-framed), or **Incorrect** (factually wrong against the code).

---

## 0. Preamble — this is a good review

Before the defense: the review is thorough, fair, and largely accurate. It
correctly identifies the load-bearing design decisions (two-layer isolation,
write-time policy application, gapless feed), it does not invent critical
vulnerabilities to look impressive, and most of its findings are real. This
response is not a rebuttal of its quality — it is a precision pass that
separates the points we will act on from the ones that are mis-weighted or
mistaken, with evidence for each.

Net result of the re-verification: **3 findings confirmed and accepted, 4
overstated, 1 factually incorrect, and — notably — 1 conclusion that is more
generous than we ourselves would claim.**

---

## 1. The one factual error: "empty `AspNetCore.Tests` project"

The review's single **CRITICAL** test-coverage item, repeated in §2.3, §7.1
("**ASP.NET Core layer — 0 tests — entire project empty**") and §8 rec. 8, rests
on a misread.

**The facts:**

- `Papuma.Kernel.slnx` contains **exactly one test project**:
  `tests/Papuma.Kernel.Tests`. There is **no** `Papuma.Kernel.AspNetCore.Tests`
  project in the solution.
- A directory `tests/Papuma.Kernel.AspNetCore.Tests/` exists on disk, but it
  contains **only `bin/` and `obj/`** — build artifacts of a project that was
  removed during the Phase-10 cleanup. There is not a single `.cs` file in it.
  The review interpreted these leftover folders as "an empty project with zero
  tests."
- The ASP.NET Core layer **is** tested: `DashboardTests` in
  `Papuma.Kernel.Tests` exercises the metrics collector and the dashboard data
  endpoint against a real PostgreSQL 18 container, and the test project
  references `Papuma.Kernel.AspNetCore` directly.

**Classification: Incorrect.** There is no empty project. Escalating a leftover
`obj/` folder to a CRITICAL finding is not warranted.

**What is *legitimately* true** (and the review would have been right to say):
the AspNetCore layer is tested **partially** — the dashboard pipeline yes, but
`ScopeMiddleware` and the lag health-check threshold have no dedicated tests.
That is a real gap (see §4 below), just not a "CRITICAL empty project." We will
delete the orphaned directory and add the middleware/health-check tests.

---

## 2. Confirmed findings — we agree and will act

### M1 — Error messages stored verbatim in `papuma.failure` ✅ Confirmed (severity fair as Low–Medium)

Verified: `ProcessHandlerBatchAsync` catches a handler exception and calls
`RegisterFailureAsync`, which writes `@error` (the exception's `TypeName: Message`)
verbatim into `papuma.failure.last_error` (ChangeFeedProcessor.cs:366, 474–480).
A handler author who lets PII into an exception message has it persisted.

**Why we accept it:** it is a genuine defense-in-depth gap. The dashboard and
MCP surface that column, and `papuma.failure` carries no RLS (it is
infrastructure, no `tenant_id`).

**Why the severity is at the lower edge of Medium:** the *kernel's* privacy
guarantee is about the **feed** (diffs/event payloads), and that guarantee holds
— policies are applied at write time and the failure path never touches document
content, only the handler's own thrown string. Whether a handler leaks PII into
its own exception text is application code behavior, the same class of risk as a
handler that `Console.WriteLine`s a customer record. Still worth a mitigation:
we will store exception **type + truncated, path-stripped message** and keep the
full detail in the logger (which the consumer already governs).

### M4 — `SET LOCAL` degrades outside a transaction ✅ Confirmed (but structurally guarded on the kernel path)

Verified and — credit to the review for reading carefully — already documented
at length in `ScopeConnectionExtensions.SetScopeAsync`. The concern is real for
**direct external callers** of that public extension method.

**The mitigating fact the review under-weights:** on every kernel-internal path,
`SetScopeAsync` is called *only* from `EnsureTransactionAsync`, which opens the
transaction immediately before. The invariant is therefore structurally upheld
wherever the kernel itself uses it; the exposure is limited to a consumer who
takes the public extension method and calls it on a raw connection outside a
transaction.

**Action:** we accept rec. 4 — add a guard that throws if
`connection.State`/no ambient transaction, converting a documented foot-gun into
a typed error. Cheap, strictly better.

### M6 — DDL uses identifier interpolation, not parameters ✅ Confirmed (severity fair, mitigation partly infeasible)

Verified: `BuildKeyIndexDdl` interpolates `key.IndexName`, the `#>>` path literal
and `metadata.Name` into `CREATE INDEX … WHERE document_type = '{metadata.Name}'`
(SchemaManager.cs:92–96). The values are all validated
(`InputValidator.ValidateDocumentType`, regex `^[A-Za-z][A-Za-z0-9_]*$` with a
100 ms ReDoS timeout), and the method's own XML doc states "no user-supplied free
text reaches the DDL."

**Where we push back on the *recommendation*, not the finding:** rec. 6 suggests
"use `NpgsqlParameter` for DDL where possible" or "`FORMAT()`". **PostgreSQL does
not accept bind parameters in DDL** — `CREATE INDEX` cannot be parameterized over
ADO.NET at all, and `FORMAT()` with `%I`/`%L` only exists inside PL/pgSQL, not in
a plain `CommandText`. So the *only* available defense for identifier injection
here is exactly what is in place: a strict allow-list validator applied to every
segment. The finding (defense rests on the validator) is correct; the implied
"just parameterize it" fix is not actually achievable for index DDL. We will
instead **harden the validator's contract** (make it the single, audited choke
point and add a test that proves a crafted type name is rejected before reaching
DDL).

---

## 3. Overstated findings — real, but mis-weighted

### M5 — `RecordFailure` exception-filter pattern ⚠️ Overstated (it is idiomatic, not "fragile by design")

The pattern `catch (Exception ex) when (RecordFailure(activity, ex))`, with
`RecordFailure` always returning `false`, is a **well-known, idiomatic .NET**
technique: it tags the `Activity` with the error *without* catching the exception
(which would disturb the savepoint/rethrow flow). It is used deliberately so the
span records the failure while the exception still propagates to the engine's
retry logic.

The review's argument — "if someone modifies `RecordFailure` to return `true`,
exceptions would be silently swallowed" — is true, but it is true of **any**
predicate method anywhere: "if someone changes the code to do the wrong thing,
the wrong thing happens" is not a property specific to this construct. By that
standard every boolean helper is "fragile."

**Classification: Overstated** (it is a code-readability preference, not a
security finding). That said, rec. 5 (replace with explicit
`try { … } catch { RecordFailure(…); throw; }`) is a **reasonable clarity
improvement** and we may adopt it — but it should be filed under code style, not
under a security severity. We will add a code comment documenting the deliberate
filter pattern either way.

### B1 — "Sequential handler processing" as a **HIGH** bottleneck ⚠️ Overstated framing

The measurement is ours and it is real (4 projection handlers → ~312/s each,
`FeedThroughputProbe`). But the **HIGH** rating and the "2.88× oversubscription"
framing assume a worst case as if it were typical:

- The number is four handlers that **all** do a SQL upsert. Realistic systems
  run a mix — often 1–3 projections plus cheap handlers (a SignalR push is
  ~microseconds, a no-op-ish translator likewise); the engine ceiling for those
  is ~27,000/s.
- "900 writes/s ⇒ lag accumulates" only holds if all heavy handlers sit
  *permanently* at the write rate. Burst load drains afterwards — and the lag is
  observable the entire time (that is the whole point of the lag gauge and health
  check).
- It is explicitly a **documented** design choice with a **measured trigger and a
  designed escape route** (`concepts.md §14`: `Task.WhenAll` parallelization,
  gated on `cycle.duration ≫ max(handler.duration)`).

A scaling ceiling that is documented, measured, observable, and has a planned
fix with an objective trigger is a **known limit**, not a HIGH-severity
bottleneck. Classification: real, **severity Medium at most**, and the planned
parallelization (rec. 7) is already on the post-1.0 list with that exact trigger.

### M3 — `ScopeMiddleware` trusts the resolver ⚠️ Overstated as a *finding* (it is the documented boundary)

Verified: `ScopeMiddleware.InvokeAsync` calls `resolver.Resolve(context)` and
stores the result. The review is correct that a *bad resolver* (mapping an
unauthenticated header to a tenant) enables tenant switching.

But this is the **designed framework/application boundary**, which the review
itself acknowledges twice — §6.3 lists Authentication and Authorization as
"✗ Delegated — consumer must implement (documented)." Counting the same
delegation once as a MEDIUM security finding and once as an accepted documented
boundary is double-billing. `IScopeResolver` is, by contract, a *mapping*
interface; authorizing the principal is the application's job, exactly as
authenticating the request is.

**Classification: Overstated as a defect; accepted as a DX opportunity.** Rec. 3
(an `IAuthenticatedScopeResolver` overload receiving the `ClaimsPrincipal`) is a
genuinely nice ergonomics addition that makes the secure path the obvious path —
we will consider it. But the current design is not insecure; it is unopinionated
at a layer where being opinionated would be wrong.

### L7 — `JsonPathResolver` ignores `[JsonIgnore]` ⚠️ Overstated (it is fail-safe, not a weakness)

Verified: `JsonNameOf` honors `[JsonPropertyName]` but the policy scan does not
skip `[JsonIgnore]` properties, so a policy *can* be declared on a path that
never appears in serialized JSON.

**Why this is close to a non-issue:** the consequence is a policy that is a
**harmless no-op** — the field is not in the document, so there is nothing to
protect, leak, or mis-handle. This is **fail-safe**: an ineffective privacy
policy on a non-existent field cannot expose data, because the data isn't there.
The review rates it Low, which is already generous; it is really a tidiness item.
We will add `[JsonIgnore]` skipping (rec. 11) so the inventory does not list
phantom paths — a correctness-of-reporting fix, not a security fix.

---

## 4. Legitimate gaps we accept beyond the findings

The review is right about these, and they are the most useful part of it:

- **Middleware & health-check tests** — `ScopeMiddleware` and the lag-threshold
  behavior of `ChangeFeedLagHealthCheck` have no dedicated tests. We will add
  them (and delete the orphaned `AspNetCore.Tests` artifact directory that
  caused the §1 confusion).
- **Concurrency/error-recovery tests** — beyond `BoundedCounter_Concurrent…`,
  there is no coverage for deadlock retry or connection-pool exhaustion. Fair;
  worth adding as the library approaches real production use.
- **Dashboard auth (M2)** — accurate. The dashboard is unauthenticated by default
  and documented as requiring `.RequireAuthorization()`. We will add a startup
  log warning when it is mapped without an auth metadata marker, per rec. 2 — a
  pit-of-success nudge.

---

## 5. Where the review is too *generous* (we correct it against ourselves)

Intellectual honesty cuts both ways. The review's §9 verdict —
**"Production-ready with recommended mitigations"** — is **more optimistic than
our own stated position.** The project's own `factsheet.md` says plainly:
`1.0.0-preview`, **has not yet carried production traffic**, best fit today is
internal line-of-business systems and teams that control their PostgreSQL
version, and regulated/mission-critical workloads should run a pilot first.

A clean code review cannot confer production-readiness that **operational**
maturity has not yet earned: zero production deployments, a bus factor of one,
no security-response process, no LTS commitment, and a hard dependency on
PostgreSQL 18 (which many enterprises have not yet adopted). The *code* is
production-grade; the *project* is pre-production. We stand by the more
conservative self-assessment.

---

## 6. Minor factual corrections

- **§5.2 / §8:** the redaction audit field is `actorId`, not `operatorId` (review
  §3.2 strength #5). The metadata block is `{ redactedAt, reason, correlationId,
  actorId, paths }`.
- **Test count:** the review says "120+ / ~120 test methods." The current suite
  is **132 `[Fact]`/`[Theory]` methods** (the runner reports 147 including
  parameterized cases). The coverage is slightly higher than credited.
- **§3.3 dependencies:** Npgsql is pinned to a specific version (`10.0.3`), not
  "latest via NuGet."

---

## 7. Verdict on the review

| Review finding | Our classification |
|---|---|
| M1 — error message storage | **Confirmed** (Low–Medium); mitigation planned |
| M2 — dashboard unauth by default | **Confirmed** (Medium); startup warning planned |
| M3 — ScopeMiddleware trust | **Overstated** as defect; DX improvement considered |
| M4 — SET LOCAL outside tx | **Confirmed** (Medium); guard planned, kernel path already safe |
| M5 — RecordFailure filter | **Overstated** (style, not security); comment + optional refactor |
| M6 — DDL interpolation | **Confirmed** (Medium); "just parameterize" fix infeasible, validator hardened |
| B1 — sequential handlers | **Overstated** (Medium, not HIGH); documented, measured, fix triggered |
| L1–L6 | Fair Low items; mostly elegance/operational notes |
| L7 — JsonIgnore | **Overstated**; fail-safe no-op, tidiness fix |
| "Empty AspNetCore.Tests project" (CRITICAL) | **Incorrect**; orphaned `obj/`, no such project |
| "Production-ready" verdict | **Too generous**; pre-production per our own factsheet |

**Bottom line:** the review correctly found that there are no critical or
high-severity *security* vulnerabilities, and its real findings (M1, M4, M6, the
test gaps, dashboard auth nudge) are worth acting on — we accept those. The
medium tier is somewhat inflated by counting documented design boundaries and a
deliberate idiom as defects, and the single CRITICAL test item is a misread of a
leftover build folder. Adjusted honestly, the picture is: **a security-sound
codebase with a short, concrete hardening list and a still-pre-production project
maturity** — which is exactly where a `1.0.0-preview` should be.
