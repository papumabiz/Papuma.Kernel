# Papuma.Kernel — Security & Architecture Review

**Date:** 2026-06-12  
**Scope:** `src/Papuma.Kernel`, `src/Papuma.Kernel.AspNetCore`, `benchmarks/`, `tests/`, `samples/`, `docs/vNEXT/`  
**Methodology:** Full code review (64 production files, 19 test classes, 2 benchmark suites, 1 sample application, 27 documentation files)

---

## 1. Executive Summary

Papuma.Kernel is a .NET 10 library implementing **Document-Sourced CQRS** — a design where JSON documents are the source of truth and an immutable change feed is derived automatically. It targets PostgreSQL ≥ 18 exclusively and provides built-in tenant isolation, privacy policies, GDPR tooling, change feed processing, and an event log. The codebase is mature, well-structured, and demonstrates strong security awareness across all layers.

**Overall Security Posture: STRONG.**  
No critical vulnerabilities found. Six medium-severity findings and eight low-severity findings identified.

**Overall Architecture Quality: HIGH.**  
The design consistently places mechanisms in the kernel and domain-legal decisions in the application. The test suite covers 120+ test methods across core functionality, with a notable gap in the ASP.NET Core layer (zero tests).

---

## 2. Architecture Assessment

### 2.1 Core Architecture Pattern

```
C# class → JSON document (truth)  
         → Change kernel (diff, version, policies)  
         → PostgreSQL (document + change feed, one transaction)  
         → IChangeHandler implementations (projections, search, audit, integration)
```

This inverts classic event sourcing: the document is the truth, and the change feed is a derived artifact. No event replay needed for current state — `LoadAsync<T>` hits the document table directly.

### 2.2 Architectural Strengths

| Strength | Detail |
|----------|--------|
| **Single-package discipline** | No provider abstractions, no plugin architecture — everything in one library for a single PostgreSQL 18 backend. Reduces attack surface and complexity. |
| **Privacy by design** | Five-tier policy catalog (Track/Redact/Reference/Hash/DoNotTrack) applied at write time, before the immutable feed. Erasure = delete the document, feed untouched. |
| **Two-layer tenant isolation** | Explicit `WHERE scope + tenant_id` predicates (layer 1) + PostgreSQL RLS with `FORCE ROW LEVEL SECURITY` (layer 2), so even the table owner is subject to RLS. |
| **Gapless feed consumption** | `txid < pg_snapshot_xmin()` ensures no uncommitted writes are skipped. `FOR UPDATE SKIP LOCKED` provides leader coordination without distributed locks. |
| **Consistent exception taxonomy** | Every error case has a typed exception with relevant properties (`ConcurrencyException` carries expected/actual versions, `UniqueKeyViolationException` carries the key path). |
| **15 Architecture Decision Records** | Every significant design decision is documented with rationale, trade-offs, and rejected alternatives. |
| **GDPR built-in, not bolted on** | Export assembly (Art. 15/20), data inventory (Art. 30), history redaction (Art. 17), tenant-scoped operations — all kernel primitives. |

### 2.3 Architectural Weaknesses

| Weakness | Detail |
|----------|--------|
| **Sequential handler processing** | `ChangeFeedProcessor.ProcessOnceAsync` iterates handlers with a plain `foreach` loop. Four handlers each doing a SQL upsert run at ~312/s each vs. ~1,400/s for one. Parallelization would scale linearly. |
| **No cross-document transactions** | Session is the only unit-of-work boundary. No support for coordinated writes across sessions. Deliberate by ADR, but limits certain use cases. |
| **Empty `Papuma.Kernel.AspNetCore.Tests` project** | The web layer (dashboard, middleware, health checks, MCP tools) has zero test coverage. |
| **No chaos/resilience testing** | No tests for PostgreSQL restart, network partition, connection pool exhaustion, or deadlock retry. |
| **Dashboard unauthenticated by default** | The embedded dashboard at `/papuma` exposes handler names, error messages, and operational metrics with no auth. Documented as requiring consumer protection. |

---

## 3. Security Audit

### 3.1 Findings by Severity

#### CRITICAL — None found.

#### HIGH — None found.

#### MEDIUM — 6 findings

| # | Finding | Location | Detail |
|---|---------|----------|--------|
| **M1** | **Error message leakage via feed failure table** | `ChangeFeedProcessor.cs`, `EventFeedProcessor.cs` | `RegisterFailureAsync` stores `Exception.TypeName: Message` verbatim in `papuma.feed_failure`. Handler exceptions may include PII, SQL fragments, file paths, or stack-adjacent strings. The dashboard truncates to 300 chars but doesn't sanitize. |
| **M2** | **Dashboard unauthenticated by default** | `DashboardExtensions.cs` | `MapPapumaDashboard()` returns `IEndpointConventionBuilder` so consumers *can* chain `.RequireAuthorization()`, but there is no enforcement. Exposes handler names, seq numbers, lag values, and error messages to anyone who can reach the endpoint. |
| **M3** | **`ScopeMiddleware` delegated tenant resolution without enforcement** | `ScopeMiddleware.cs` | The middleware calls `IScopeResolver.Resolve(HttpContext)` and trusts the result. If the resolver implementation maps arbitrary requests to arbitrary tenants (e.g., via an HTTP header without auth), an attacker can switch tenants. The kernel's `ScopeContext` validates tenant ID format, not authorization. |
| **M4** | **`SET LOCAL` scope degradation outside transactions** | `ScopeConnectionExtensions.cs` | `SetScopeAsync` uses `set_config(..., is_local: true)` — equivalent to `SET LOCAL`, scoped to the current transaction. If called outside a transaction, `SET LOCAL` silently degrades to `SET` (session-level). This is a runtime invariant that callers must uphold. Misuse could leak tenant scope across operations. |
| **M5** | **`RecordFailure` exception filter pattern always returns `false`** | `DocumentSession.Patch.cs:110`, `DocumentSession.Gdpr.cs:102,201` | The pattern `catch (Exception ex) when (RecordFailure(activity, ex))` with `RecordFailure` always returning `false` means the exception is never caught. If someone modifies `RecordFailure` to return `true`, all exceptions would be silently swallowed in GDPR and patch operations. Fragile by design. |
| **M6** | **SQL identifier interpolation in DDL despite input validation** | `SchemaManager.cs`, `SchemaDdl.cs` | While `metadata.Name` and path segments are validated by `InputValidator.ValidateDocumentType` (alphanumeric + underscore regex with timeout), the DDL generation uses string interpolation (`$"WHERE document_type = '{metadata.Name}'"`) rather than parameterization. This relies entirely on the validator for SQL injection defense. Defense in depth recommends parameterization even for validated values. |

#### LOW — 7 findings

| # | Finding | Location | Detail |
|---|---------|----------|--------|
| **L1** | **`ScanType` cycle guard uses stack-based removal** | `KernelModelBuilder.cs:472`, `DataInventory.cs:91` | The cycle guard pattern `visited.Add(type)` / `visited.Remove(type)` is stack-based. If type A → B → A, when unwinding B, A is removed from visited. If a sibling of B also references A, A is re-scanned. Since scanning is side-effect-free (collecting metadata), this produces at most duplicate entries — not incorrect, but not elegant. |
| **L2** | **Fragile cast of `Dictionary.Values` to `IReadOnlyCollection<T>`** | `KernelModel.cs` | `(IReadOnlyCollection<DocumentTypeMetadata>)_byType.Values` depends on `Dictionary<TKey,TValue>.ValueCollection` implementing `IReadOnlyCollection<T>`. While true in current .NET, this is an implementation detail, not a documented contract. |
| **L3** | **Thread-safe but unsynchronized LRU eviction sort** | `ScopeDataSourceFactory.cs` | `LastAccessOrder` is a mutable field modified via `Interlocked.Exchange`. `EvictIfNeeded` reads it via `OrderBy` without synchronization, creating a benign data race — the sort order may be slightly stale under high concurrency. Acceptable for LRU eviction. |
| **L4** | **No CORS configuration on dashboard endpoints** | `DashboardExtensions.cs` | If the dashboard is mapped on a non-localhost binding, cross-origin requests to `/data` could leak operational metadata. Same-origin policy mitigates in most scenarios. |
| **L5** | **`DashboardMetricsCollector` disposable with no re-initialization** | `DashboardMetricsCollector.cs` | Singleton `IDisposable` with no re-initialization path. If disposed early (DI shutdown ordering), the dashboard loses metrics permanently. |
| **L6** | **`FeedThroughputProbe` uses local container — not production-representative** | `benchmarks/` | The 898 writes/s result is against a local PostgreSQL container with no network latency. Production figures will vary significantly. |
| **L7** | **`JsonPathResolver` ignores `[JsonIgnore]` attribute** | `JsonPathResolver.cs` | Policies can be declared on `[JsonIgnore]`-annotated properties, creating metadata for non-existent JSON paths. The policy would never be applied because the path doesn't exist in serialized JSON. |

### 3.2 Security Strengths (What They Got Right)

| # | Strength | Detail |
|---|----------|--------|
| 1 | **100% parameterized queries** | Every SQL query uses `NpgsqlParameter`. No string concatenation for values anywhere in the codebase. |
| 2 | **Privacy policies at write time** | `PolicyApplier` transforms diffs *before* they enter the immutable change feed. Redacted = never stored in clear text. |
| 3 | **Input validation with ReDoS protection** | All regex validators use `TimeSpan.FromMilliseconds(100)` timeout. Tenant IDs, document types, and index paths are all validated. |
| 4 | **`FORCE ROW LEVEL SECURITY`** | Even the table owner (superuser or app role) is subject to RLS on `papuma.document`, `papuma.change`, and `papuma.event`. |
| 5 | **GDPR redaction with audit trail** | `RedactHistoryAsync` rewrites historical diffs with mandatory `reason`, `operatorId`, `redactedAt` metadata. Irreversible by design. |
| 6 | **Rollback safety** | `RollbackNotPossibleException` prevents silent wrong-state reconstruction when diff values are redacted/hashed. |
| 7 | **MCP mutations gated by opt-in** | `AllowMutations` flag required for `retry_feed_failure`/`reset_feed_checkpoint`. Default is read-only. |
| 8 | **Bulk operations: no free-form WHERE** | Predicates limited to declared keys (index-backed). Prevents unauthorized mass data access. |
| 9 | **Sensitive data exclusion from observability** | Spans tag document IDs and tenant, never contents. Diffs are policy-applied before they reach any observer. |
| 10 | **NATS bridge payload sanitization** | Published payloads use `change.Diff.ToJson()` — the already policy-applied diff. "The bus cannot leak secrets." |

### 3.3 Dependency Security

| Dependency | Version | Risk |
|------------|---------|------|
| Npgsql | latest via NuGet | Well-maintained PostgreSQL ADO.NET provider |
| Microsoft.Extensions.Logging | framework | BCL, minimal risk |
| Microsoft.Extensions.DependencyInjection | framework | BCL, minimal risk |
| Microsoft.Extensions.Hosting | framework | BCL, minimal risk |
| System.Text.Json | framework | BCL, minimal risk |
| System.Security.Cryptography | framework | BCL, minimal risk |
| BenchmarkDotNet | benchmark-only | Dev dependency, not shipped |
| Testcontainers.PostgreSql | test-only | Dev dependency, not shipped |

**Zero third-party runtime dependencies beyond Npgsql.** This is an excellent security posture.

---

## 4. Code Quality & Code Smells

### 4.1 Code Smells

| # | Smell | Location | Detail |
|---|-------|----------|--------|
| **CS1** | **Stack-based visited set** | `KernelModelBuilder.cs:472`, `DataInventory.cs:91` | `visited.Remove(type)` after recursion is a stack-based cycle guard, not a true visited-set. Re-scans types with diamond dependencies. Minor correctness issue for metadata scanning. |
| **CS2** | **Fragile exception filter pattern** | `DocumentSession.Patch.cs:110`, `DocumentSession.Gdpr.cs:102,201` | `catch (Exception ex) when (RecordFailure(activity, ex))` with `RecordFailure` returning `false` — clever but fragile. If modified to return `true`, exceptions are silently swallowed. |
| **CS3** | **SQL view security caveats in docs only** | `docs/vNEXT/architecture.md` | Views must use `security_invoker = on` (PostgreSQL ≥ 15) or RLS is silently bypassed. This is documented but not enforced in code. |
| **CS4** | **`KernelJson` shared static instance** | `KernelJson.cs` | Thread-safe since `JsonSerializerOptions` is documented as thread-safe after construction, but a shared mutable config object is worth documenting explicitly. |
| **CS5** | **Duplicate pattern in GDPR export and DocumentSession** | `GdprExport.cs` | `AddIdentityParameters` duplicates the identity parameter binding pattern from `DocumentSession`. Acceptable for separation of concerns but worth noting. |

### 4.2 Dead Code / Unused Code

None found. All public types and methods are either called by internal consumers or exposed as part of the public API.

### 4.3 Documentation Quality

**Excellent.** Every public type and method has XML documentation. 15 Architecture Decision Records provide context, rationale, and rejected alternatives. The `docs/vNEXT/concepts.md` provides 22 sections of narrative explanation. The sample application's `README.md` maps every kernel concept to running code.

---

## 5. Bottlenecks & Performance

### 5.1 Identified Bottlenecks

| # | Bottleneck | Impact | Benchmark Evidence |
|---|-----------|--------|-------------------|
| **B1** | **Sequential handler processing** | **HIGH** — Four projection handlers drop to 312/s each vs. 1,416/s for one. With 900 writes/s, lag accumulates at 2.88× oversubscription. | `FeedThroughputProbe`: 1 handler = 1,416/s, 4 handlers = 312/s each |
| **B2** | **Per-change SQL connection in handlers** | **MEDIUM** — ~0.7 ms per SQL upsert dominates handler cost. Connection pooling mitigates physical open but not logical open overhead. | `FeedThroughputProbe`: no-op handler = 26,904/s vs. SQL handler = 1,416/s |
| **B3** | **`DeepClone` allocations on every write** | **MEDIUM** — 0.8–1.9 MB allocated per write for 10,000-leaf documents. Linear in document size. | `DiffEngineBenchmarks`: 10,000 leaves = 0.8–1.9 MB |
| **B4** | **Diff engine cost dominated by tree walk** | **LOW** — A no-change save costs ~same as a 10%-changed save. The tree traversal is the dominant cost, not the diff computation. | `DiffEngineBenchmarks`: 10,000 leaves no-change = 4.0 ms, 10% changed = 3.7 ms |
| **B5** | **Dashboard polls both feeds sequentially** | **LOW** — `DashboardDataBuilder` calls `GetLagAsync` for change feed, then event feed, sequentially. Each opens a DB connection. | Code analysis |

### 5.2 Performance Guidance (from benchmarks)

- Keep documents under ~1,000 leaf fields for normal operation.
- Never exceed ~10,000 leaf fields — remodel the aggregate instead.
- Use bounded counters (`Increment`) for stock/balance operations — atomic in SQL, no read-then-write race.
- Handler idempotency with `expectedVersion: 0` + `ConcurrencyException` swallowing prevents at-least-once duplicates.

---

## 6. Attack Surface Analysis

### 6.1 Entry Points

| Entry Point | Exposure | Risk |
|-------------|----------|------|
| `DocumentSession` public API | Application code only | Low — not externally accessible |
| `DocumentStore` public API | Application code only | Low — not externally accessible |
| `POST /products`, `POST /orders`, etc. (Sample) | HTTP endpoints | Depends on consumer auth |
| `GET /papuma/` dashboard | HTTP endpoint | Medium — unauthenticated, exposes operational metadata |
| `GET /papuma/data` dashboard data | HTTP endpoint | Medium — unauthenticated JSON with handler errors |
| `GET /health` health check | HTTP endpoint | Low — typical health check exposure |
| `GET/POST /mcp` MCP server | HTTP endpoint | Low — read-only by default, mutations require opt-in |
| PostgreSQL RLS policies | Database | Low — enforced at DB level |
| NATS subjects (optional) | Message bus | Low — tenant-scoped subjects, policy-applied payloads |

### 6.2 Attack Vectors

| Vector | Mitigation | Residual Risk |
|--------|-----------|--------------|
| **SQL injection** | 100% parameterized queries | None |
| **Tenant impersonation** | Two-layer isolation (WHERE + RLS) | Low — if RLS is bypassed, explicit WHERE predicates remain |
| **ReDoS** | Regex timeouts (100ms) | None |
| **Concurrency attacks** (lost updates) | `expectedVersion` mandatory on saves | None |
| **Memory exhaustion** | Large documents → large diffs → GC pressure | Low — document size guidance exists |
| **Information disclosure via errors** | Exception mapping to typed exceptions | Medium — handler errors stored verbatim |
| **Information disclosure via dashboard** | No auth by default | Medium — consumer must add auth |
| **Information disclosure via NATS** | Policy-applied diffs only | None |
| **Denial of service via large patches** | Bulk operations produce one ChangeRecord per document | Low — extremely large bulk could still stress DB |

### 6.3 Defense in Depth Assessment

| Layer | Status |
|-------|--------|
| **Input validation** | ✓ Strong — regex with timeouts, length limits, type validation |
| **Parameterized queries** | ✓ Complete — no value concatenation anywhere |
| **Row-Level Security** | ✓ Strong — FORCE ROW LEVEL SECURITY on all tenant tables |
| **Privacy policies** | ✓ Strong — applied at write time, before immutable feed |
| **GDPR tooling** | ✓ Comprehensive — export, inventory, redaction, tenant isolation |
| **Optimistic concurrency** | ✓ Strong — expectedVersion on every write |
| **Error handling** | ✓ Good — typed exceptions, no raw DB errors leaked to callers |
| **Authentication** | ✗ Delegated — consumer must implement (documented) |
| **Authorization** | ✗ Delegated — consumer must implement (documented) |
| **Rate limiting** | ✗ Not present — consumer must implement |
| **CORS** | ✗ Not configured — consumer must implement |
| **Audit logging** | ✓ Good — actorId, causationId, correlationId on every change |

---

## 7. Test Coverage Assessment

### 7.1 Coverage by Area

| Area | Tests | Coverage Quality |
|------|-------|-----------------|
| Diff engine | ~12 | Excellent — round-trips, type changes, edge cases |
| Document session | ~12 | Good — atomicity, versioning, isolation |
| Policy & keys | ~9 | Excellent — all five policies, unique key violations |
| Schema evolution | ~6 | Good — upcaster chain, lazy loading, version conflicts |
| Patch & bulk | ~14 | Excellent — bounded counter concurrency, atomicity, empty results |
| Session/UoW/rollback | ~9 | Excellent — rollback safety, dispose behavior |
| Change feed | ~10 | Good — gapless reads, NOTIFY wakeup, poison handling, rebuild |
| Event log | ~7 | Good — retention, correlation, policy rejection |
| GDPR | ~10 | Excellent — export, redaction, tenant isolation, idempotency |
| MCP tools | ~4 | Adequate — mutation gating, policy application |
| RLS isolation | ~5 | Good — cross-tenant, missing scope, non-superuser |
| Scope context | ~6 | Good — injection rejection, length limits |
| Observability | ~5 | Good — lag metrics, counters, trace propagation |
| Dashboard | ~2 | Minimal — only lag query and history endpoint |
| Hosting integration | ~2 | Minimal — only DI registration check |
| **ASP.NET Core layer** | **0** | **None — entire project empty** |

### 7.2 Missing Test Coverage

| Priority | Area | What's Missing |
|----------|------|---------------|
| **CRITICAL** | `Papuma.Kernel.AspNetCore.Tests` | Entire project has zero tests — middleware, dashboard, health checks, MCP tools |
| **HIGH** | Concurrent stress testing | Only one test (`BoundedCounter_ConcurrentBuyers_NeverOversell`) with 12 concurrent sessions |
| **HIGH** | Error recovery paths | No tests for connection pool exhaustion, deadlock retry, serialization failures |
| **MEDIUM** | Chaos/resilience | No tests for PostgreSQL restart, network partition, NOTIFY after reconnect |
| **MEDIUM** | Edge cases | Upcaster exceptions, circular upcasters, empty PatchBuilder, 0-handler feed processors |
| **LOW** | Memory/load testing | No tests for large document behavior, allocation patterns, or connection pool saturation |

---

## 8. Recommendations

### 8.1 Security (Priority-Ordered)

1. **Add sanitization to feed failure storage** (M1) — Strip stack traces and potentially sensitive data from `RegisterFailureAsync` error messages. Store only the exception type name and the first 200 characters of the message after removing paths.

2. **Add `.RequireAuthorization()` by default or require opt-out** (M2) — Either default the dashboard to require authorization, or at minimum produce a build warning/analyzer when `MapPapumaDashboard()` is called without a subsequent `.RequireAuthorization()`.

3. **Add an auth integration point to `IScopeResolver`** (M3) — Consider adding an `IAuthenticatedScopeResolver` that receives the authenticated user principal in addition to `HttpContext`. The existing `IScopeResolver` is purely a mapping interface.

4. **Add transaction assertion to `SetScopeAsync`** (M4) — Throw `InvalidOperationException` if called outside a transaction. The workaround is to check `NpgsqlConnection` transaction state before setting scope session variables.

5. **Replace `RecordFailure` exception filter with explicit try/catch** (M5) — Use `try { ... } catch (Exception ex) { RecordFailure(activity, ex); throw; }` instead of the exception filter pattern. Same behavior, clearer intent.

6. **Parameterize schema DDL identifier parts** (M6) — While `ValidateDocumentType` prevents injection, using `NpgsqlParameter` for DDL where possible adds defense in depth. For the `WHERE document_type = '{name}'` patterns, consider `FORMAT()` or equivalent.

### 8.2 Architecture

7. **Parallelize handler processing** — Add a `Task.WhenAll` option to `ChangeFeedProcessor` for handlers that don't need sequential ordering. Benchmark shows 4 SQL handlers drop from 1,416/s (1 handler) to 312/s each (4 handlers) due to sequential execution.

8. **Fill `Papuma.Kernel.AspNetCore.Tests`** — The entire web layer has zero test coverage. At minimum: middleware scope resolution, dashboard data endpoint, health check threshold behavior.

9. **Add chaos/resilience tests** — PostgreSQL restart during processing, connection pool exhaustion, deadlock retry behavior.

### 8.3 Code Quality

10. **Replace stack-based visited set with `HashSet`** (CS1) — Use a top-level `HashSet<Type>` per `ScanType`/`Walk` call that is never removed from, or pass the set by reference with proper scoping.

11. **Add `[JsonIgnore]` handling to `JsonPathResolver`** (L7) — Skip properties with `[JsonIgnore]` during policy scanning to avoid declaring policies on non-existent JSON paths.

### 8.4 Documentation

12. **Add SQL view security caveats to `SchemaManager` XML docs** — Currently only in `docs/vNEXT/architecture.md`. Should be visible when reading the DDL code directly.

13. **Document `KernelJson` thread safety explicitly** — The shared static `JsonSerializerOptions` instance is thread-safe per Microsoft docs, but this should be noted in XML comments.

---

## 9. Conclusion

Papuma.Kernel is a **well-architected, security-conscious library** with strong privacy-by-design principles and comprehensive GDPR tooling. The codebase demonstrates maturity through extensive testing (120+ tests), architectural decision records (15 ADRs), and clear separation of concerns.

The security posture is **strong with no critical or high-severity findings**. The six medium-severity issues are all mitigable with modest effort — primarily around tightening the error handling pipeline and adding default protections to the dashboard endpoint.

The primary architectural concern is the **sequential handler processing bottleneck**, which is a deliberate design choice (simpler failure semantics) but becomes a scaling ceiling at ~4 projection handlers with 900 writes/s.

The most significant gap is the **completely untested ASP.NET Core integration layer** — the middleware, dashboard, health checks, and MCP tools have zero automated test coverage despite being the primary external-facing integration points.

**Overall Assessment: Production-ready with recommended mitigations.**
