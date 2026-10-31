# ADR-016: Policy-projected reads and the content MCP boundary

## Status

Accepted (2026-06-13) · Implemented (2026-06-13) · Amended (2026-10-20: key-lookup limitation, §5)

## Context

The diagnostics MCP server (phase 13, `Papuma.Kernel.Mcp`) is safe for AI agents
*by construction*: it exposes only policy-**applied** data — change history where
`[SensitiveData]` is already `{"changed": true}`, the metamodel inventory, lag and
failures. That is exactly what makes the feed "safe LLM reading material"
(ai-consumers recipe, concepts §21).

There is genuine demand for a step further: letting an agent read the **current
state** of documents — support ("show me order 4711"), debugging, natural-language
data access. But the document store holds **plain text**: privacy policies act on
the derived feed, not on `papuma.document.data` (ADR-007, concepts §23). A naive
"load and return the document" MCP tool would therefore hand `email`, `iban`,
password-adjacent fields directly to an LLM — the exact inverse of the guarantee
the diagnostics server upholds.

Two further temptations come with the request: a generic "search documents by
field" capability (the query-DSL trap ADR-006/009 reject), and a generic "query my
projections" capability.

## Decision

### 1. A new primitive: the policy-projected read

Privacy policies (ADR-007) are generalized from a *diff transformation* into a
transformation that also applies to a **whole document on read**. A
policy-projected read (`LoadMaskedAsync<T>` / a masked load by key) returns the
document with field policies applied, under one invariant:

> **A policy-projected read shows exactly what the feed shows — never more.**

Concretely, per field policy:

| Policy | Diff (existing) | Policy-projected read |
|---|---|---|
| `Track` | values verbatim | value verbatim |
| `Redact` (`[SensitiveData]`) | `{"changed": true}` | field masked / omitted |
| `Hash` (`[TrackHash]`) | marker + keyed hash (ADR-025) | value replaced by the same keyed hash |
| `Reference` (`[TrackReference]`) | `{"ref": …}` | field masked / omitted |
| `DoNotTrack` | absent | field omitted |

The conservative rule: **only `Track` fields appear in clear text; everything that
is not in clear text in the feed is not in clear text in a projected read either.**
This extends the write-time minimization to the read path and unifies one policy
definition across both (concepts §24).

The plain `LoadAsync<T>` (full clear-text state) remains the in-process API for
business logic, strong-consistency reads and the application's own authorization
boundary. The masked read is the variant exposed to *untrusted-ish* consumers
(AI agents, support tooling).

### 2. The content MCP tools

`Papuma.Kernel.Mcp` gains read-only, scope-bound content tools built on the
policy-projected read:

- `get_document` — load one document by id, masked.
- `get_document_by_key` — load by a **declared key** (ADR-006), masked.

No free-form query, no arbitrary field filter — lookups are exactly as powerful as
the declared keys, no more (consistent with ADR-006/009). Richer selection is the
application's job (a projection, then its own tool).

### 3. Opt-in per document type, read-only, secure default

Exposure is **not** global. A document type is readable over MCP only when the
model opts it in (e.g. `d.ExposeToMcp()`); the default is not exposed. The opt-in covers
every tool that shows a document's content (amendment, review M-2, 2026-10-31):
`get_document_history` refuses a type that is not exposed — its diffs reconstruct the
unprotected fields as well as a read does — and `get_changes_by_correlation` lists the
changes of such a type with type, id, version and operation only (`"exposed": false`, no
diff, no metadata), so the agent sees what a command touched but not what changed. There is
**no write path** over MCP — writes go through the session (validation, versioning,
diff, policies); an MCP write would be a different and much larger risk class.

### 4. No generic projection MCP

The kernel will not offer a generic "query projections" MCP tool. Projections are
dumb handlers writing into application-owned stores with their own schemas and
query languages (ADR-009) — the kernel does not know them and cannot expose them
generically. Application-specific projection tools are the consumer's to build;
the kernel provides the pattern (a recipe), not the generic instrument. This is
the same mechanism-vs-application boundary as everywhere else.

### 5. A key lookup on a protected field is an existence oracle (review S-15) — refused over MCP

The masked read masks the **result**; the **lookup** compares the clear-text value. If a
declared key is also a protected field — `Email` is `[UniqueKey]` *and* `[SensitiveData]`,
the common case — an agent that may call `get_document_by_key` can probe arbitrary values:
"no document" versus "a document (with `email` masked)" tells whether that address is a
customer, without any protected value ever being returned. For a non-unique lookup key the
multi-match error additionally reveals that more than one document carries the value
(`LIMIT 2` + exception).

**Decided 2026-10-31 (amendment):** `get_document_by_key` refuses a key whose effective policy
is not `Track` (`Redact`, `Hash`, `Reference`, `DoNotTrack`, also inherited from an ancestor).
The application keeps `LoadByKeyAsync`/`LoadMaskedAsync` in-process; only the agent loses the
probe. Keys on unprotected fields stay an oracle for exactly their unprotected value, which the
agent could read anyway — expose a type only if *finding* its documents by those keys is
something the agent's principal may do. The oracle never crosses tenants (section 6).

### 6. The scope comes from the caller, not from the agent (amendment, review M-1, 2026-10-31)

The scope-bound tools took the tenant as a tool argument, as given: a prompt injection could
read any tenant whose ids it learned, and the platform scope by omitting the argument. The
scope is now the operator's: `PapumaMcpOptions.ScopeResolver`
(`Func<McpScopeRequest, ScopeContext?>`) is called per tool call with the call's services
(under HTTP the request's, so `IHttpContextAccessor` reaches the principal) and the scope the
agent asked for (`Requested`, untrusted). The tool reads in the scope the resolver returns. A
`tenantId` naming another scope is refused, without disclosing the bound one; an omitted
`tenantId` means the bound scope, no longer "platform"; `null` refuses the call. **Without a
resolver the scope-bound tools refuse every call** (fail closed); `get_model_inventory`,
`get_feed_lag` and `get_feed_failures` are unaffected — they read no documents.

An operator tool on a management port, whose every caller may read every tenant, restores the
agent-chosen scope deliberately: `ScopeResolver = McpScopeResolvers.TrustToolArgument`. That
is a decision in the code, not a default.

## Consequences

- One policy definition, two enforcement sites (write-time diff + read-time
  projection) — no second place for a field's sensitivity to drift.
- The "safe LLM reading material" property now covers state, not just history;
  an agent with the content tools structurally cannot read a protected field.
- Masking on read is **not** an authorization mechanism — it is data
  minimization. Authorizing *which* documents an agent may read at all remains the
  application's responsibility (scope binding + the MCP host's auth), exactly as
  with the dashboard (review M2) and `IScopeResolver` (review M3).
- `Hash`/`Redact` on read are lossy by design: the masked read cannot reconstruct
  the value (the same one-way property as the feed, concepts §8). Business logic
  that needs the real value uses `LoadAsync` in-process, never the MCP path.
- Cost: a second load path and a per-type opt-in flag in the metamodel. Small,
  and isolated behind the existing policy machinery.
