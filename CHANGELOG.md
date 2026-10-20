# Changelog

## Unreleased

Security:

- **Policy attributes on structs and on elements of collections are no longer silently ignored (review C-1).**
  `[SensitiveData]`, `[TrackHash]`, `[TrackReference]` and `[DoNotTrack]` were only honoured on members of
  *classes*. On a member of a **struct** (`record struct Address([property: SensitiveData] string Street, …)`,
  also as `Address?`) and on a member of a collection's **element type** (`List<Address>`) the model built without
  a word and the value went into the change feed, the event payloads and masked reads in clear text — checked
  with a real session: `"street":"SECRET-1"` in the stored diff.
  *Structs* are now scanned like classes: the policy takes effect, **a projection that relied on the
  unprotected value in the feed no longer sees it**. *Collections* cannot carry per-element policies (a collection
  is one atomic diff value, ADR-004), so `Build()` now throws `InvalidOperationException` naming the property path
  and the annotated member — **an application whose model had such an attribute stops at startup until it decides:**
  put the policy on the collection property itself (`[SensitiveData] List<Address> Homes`, or
  `.Property(x => x.Homes).Redact()`), or state `.Property(x => x.Homes).Track()` if the elements are meant to
  be stored as they are. Data written before the fix stays in the feed as it was: review it with
  `DataInventory.Build(model)` and clean up with `RedactHistoryAsync` (docs/gdpr.md). The data inventory uses the
  same type view as the scan now, so it lists the struct members it protects. Details: ADR-007 (amendment).
- **Dashboard: stored XSS fixed (review A-1).** The embedded dashboard rendered handler names, exception
  messages and metric series names into `innerHTML` unescaped, so text a tenant could get into an exception
  message ran as script in the operator's browser — also behind `RequireAuthorization()`. Every dynamic value
  now goes through `esc()`. As a second layer both the page and `{path}/data` send a
  `Content-Security-Policy` (the inline script and style carry a per-request nonce; `frame-ancestors 'none'`),
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `Cache-Control: no-store`.
  `MapPapumaDashboard(path)` now validates `path` (absolute, letters/digits/`_`/`-` segments) and throws
  `ArgumentException` for `/` and other values — `/` used to map the application root.

- **A trailing newline no longer passes the tenant-id and type-name whitelists (review C-2).** The patterns ended
  in `$`, which .NET also matches before a final `\n`: `ScopeContext.Tenant("acme\n")` was a second, phantom
  tenant with its own hash keys (a claim or header with a trailing newline), and `"Order\n"` was a valid type
  or event name. Both patterns end in `\z` now and throw `ArgumentException`. **A tenant id or type name that
  carried a trailing newline is refused from now on** — it never matched the documented pattern.
- **Event payload policies are applied deepest path first (review C-3).** With `address` hashed and
  `address.street` redacted on the same event type, the stored hash depended on the order the two policies
  entered the dictionary (attribute vs. fluent, `Track()` resets): the street was in the hash or not. The
  descendant is applied first now, so the hash covers what remains — the same result in every declaration
  order. **An event type with such a nested pair stores a different hash than before** (hashes of the same
  value in older events do not compare equal to new ones). No order ever stored clear text.
- **Redaction updates carry the scope predicate themselves (review S-5).** The two `UPDATE`s of `RedactHistoryAsync`
  and `RedactEventsAsync` addressed a row by `seq` alone and relied on row-level security for the tenant — the only
  write statements that did. They carry `AND scope = @scope AND tenant_id = @tenantId` now, like every other
  statement (layer 1 of the two-layer model). The `seq` always came from a scoped `SELECT … FOR UPDATE` in the same
  transaction, so no cross-tenant write was reachable; this removes the sole reliance on layer 2.

Fixed:

- **Event retention deletes in batches (review P-8, L-11).** `EventRetention.PurgeExpiredAsync` (PostgreSQL) removed
  everything expired in one transaction — the first purge after retention was switched on for a type with a large
  backlog held one long transaction (locks, WAL, vacuum debt, a timeout losing all of it). It now deletes 1000
  events per transaction, oldest first, and loops until the backlog is drained; cancellation is checked between
  the batches and keeps what is already committed. `SqliteEventRetention` works the same way (one
  `DELETE … LIMIT` per batch instead of one write lock for the whole purge) and uses one connection for all event
  types instead of one per type. Public signatures and return values are unchanged.

- **Two declared keys can no longer silently share one index (review C-8, N-5).** The index name is built from the
  type name and the path (`.` and the composite separator folded into `_`, everything lower-cased) and created
  with `CREATE INDEX IF NOT EXISTS`: `a.b` and `a_b`, `Order_Item` + `x` and `Order` + `item_x`, or two paths that
  differ only in case produced the same name — the second index was never created and its **UNIQUE constraint
  was never enforced**, without any error. `Build()` throws `InvalidOperationException` naming both keys now; give
  one of the types another logical name (`.Named("…")`) or rename a property.
- **A key can sit on a one-letter property or on `_id` (S-12).** The DDL validated key path segments with the
  document-type pattern, so `[UniqueKey] string X` or `[JsonPropertyName("_id")]` failed the schema step with
  "Invalid documentType". Path segments have their own validator now (`[A-Za-z_][A-Za-z0-9_]{0,100}`), and the type
  name, the segments and the index name are validated where the DDL is built, in both kernels (L-18).
- **A patch can no longer change the id (S-1).** `PatchAsync(id, p => p.Set(x => x.Id, "other"))` rewrote the id
  inside the document but not in the `id` column: `LoadAsync(id)` returned a document with a foreign id, the next
  save addressed another document and feed references named the wrong one. Set, Remove and Increment on the
  id property (`HasId` or `Id`) throw `ArgumentException` before any SQL runs — also `PatchManyAsync` and
  `PatchWhereAsync`, on both engines.
- **`HasId` validates its expression (C-5).** `x => x.Nested.Id`, a field, a method call or a constant used to fail
  later (an `InvalidCastException` in `Build()`, a `TargetException` at the first save); `HasId` throws
  `ArgumentException` (`id`) at the call now.
- **`Upcast` checks its range (C-9).** `DocumentTypeMetadata.Upcast` with a stored schema version below 1 or above
  the current one throws `ArgumentOutOfRangeException` instead of failing with a list index error or doing nothing.
- **Looking at the lag can no longer rebuild a projection (review P-4).** `GetLagAsync` — behind the lag health
  check, the dashboard and the MCP tool `get_feed_lag` (documented as non-mutating) — first registered the
  handlers, and registering reconciles projection versions: on the first call per process after a deploy it
  could run `IProjection.ResetAsync()` and reset the cursor. A web node that runs no workers and declares a
  newer version therefore emptied the projection when the load balancer probed it, while the running
  workers paused. `GetLagAsync` is a pure read now (no registration, no row lock); a handler that is not
  registered yet shows the whole backlog. Registration and rebuild happen only in `RunAsync`,
  `ProcessOnceAsync` and the reset methods. All four processors.
- **An interrupted manual projection reset is finished on the next start (P-14).** `ResetProjectionAsync` emptied
  the target and then reset the cursor in a second step; a crash in between left an empty target behind a
  checkpoint at the head, for good. A marker (stored projection version `-1`) is now committed first and
  makes the next start rebuild.
- **SQLite: a reset during a batch that started at checkpoint 0 is no longer overwritten (L-3).** The outcome of
  a batch was applied if the checkpoint still equalled the one read — and a rebuild from 0 to 0 looks
  unchanged, so the checkpoint jumped to the end of the batch although the projection had just been
  emptied: rows lost for good. The checkpoint has a generation now (`reset_count`, added to existing files by
  `EnsureSchema`; **run it once** — with `EnsureSchema = false` the new column must be added by your own
  migration: `ALTER TABLE checkpoint ADD COLUMN reset_count INTEGER NOT NULL DEFAULT 0`).
- **`ResetCheckpointAsync` and `RetryFailureAsync` accept only the processor's own handlers (P-13).** A typo
  created an orphan checkpoint row; `"event:orders"` on the change feed reached the event feed's entry. They
  throw `ArgumentException` (`handlerName`) now.
- **SQLite: `RedactEventsAsync` finds events by numeric and boolean selectors (review L-4, GDPR).**
  `json_extract(payload, path) = @value` compared a TEXT parameter with an INTEGER/REAL/0-1 value, which
  SQLite never considers equal — an erasure request for `userId = 42` returned 0 and left the personal data
  in the event log, silently. The selector is now compared as text with the JSON spelling of the field,
  exactly like the PostgreSQL kernel's `#>>` (`42`, `true`, `1.50`; a number beyond 2^53 is not rounded;
  JSON `null` and a missing field match nothing). Both engines now run the same case table.
- **SQLite: integer keys beyond 2^53 are matched exactly (L-9).** `LoadByKey`, `LoadAllByKey`, `PatchWhere`
  and `DeleteWhere` bound every number as `double`, so `9007199254740993` became `…992.0` — not found, or
  worse: the **neighbouring document** was returned, patched or deleted (snowflake ids). Integers bind as
  `long` now.
- **SQLite: stored timestamps are parsed culture-independently (L-10).** Hardening only: the suspected shift
  under `th-TH` does not occur for the `"O"` text the kernel writes.
- **A rejected write can no longer leave half of itself in the transaction (review Q-2, S-6, L-6).** Each write
  runs under a savepoint and a failing write is rolled back to it — but that rollback used the caller's
  `CancellationToken`. When the request was cancelled at the moment a write failed (after its `UPDATE` ran, e.g.
  a patch validator rejecting while the client disconnects) the rollback threw instead, the caller's exception
  was lost, and the transaction still held the write: an application that then committed ("commit regardless")
  persisted a document changed **without its change record**, which diverges every projection for good. On
  SQLite a bulk patch could be persisted half-done the same way. The rollback now ignores the token and the
  caller's own exception always surfaces. If the rollback itself fails (lost connection) the session becomes
  unusable — `CommitAsync` and every further operation throw `InvalidOperationException`; `DiscardAsync`
  recovers it when the full rollback succeeds. The failure is attached to the original exception as
  `Data["papuma.savepoint_rollback_failure"]` and logged. `DisposeAsync` (and `ScopedConnection`) now release
  the connection even when rolling back throws.
- **A handler timeout no longer stops the feed (review P-1, L-1).** An `OperationCanceledException` raised by
  a handler itself — typically the `TaskCanceledException` of an `HttpClient` timeout — was treated as
  shutdown: `RunAsync` returned, the hosted service "completed successfully", and every projection stood
  still with no failure entry, no retry and only an information-level "stopped" line. Only cancellation of
  the token passed to `RunAsync` ends the loop now; any other cancellation is recorded as a failure and
  retried. All four processors (PostgreSQL/SQLite, change/event feed).
- **No more busy loop when the database is unavailable (P-2, L-2).** A failing cycle skipped the idle wait,
  so an unreachable PostgreSQL server or an unopenable SQLite file made the worker spin — thousands of log
  lines per second and a reconnect storm. After a faulted cycle the worker now waits
  `PollInterval × 2^(n−1)` (cap 30 s) and logs the first failure, then every power of two. The SQLite lag
  query no longer takes the write lock.
- **One handler's broken batch no longer blocks the others (N-6, P-10).** A fault while loading or mapping
  one handler's batch aborted the whole cycle, leaving every later handler stuck. Handlers are isolated now;
  `ProcessOnceAsync` still reports the fault afterwards (the original exception, or an `AggregateException`
  for several). Unmappable rows raise an `InvalidDataException` naming the sequence number and handler. The
  trace span of a failed handler carries the sanitized error text, like the failure table (P-19).
- **A feed worker that ends by itself now stops the host.** If the loop returns without a shutdown request
  the hosted service throws, so `BackgroundServiceExceptionBehavior` applies instead of a silently finished
  service.

- **`Increment` outside the `decimal` range fails typed instead of lying or overflowing (review C-4).** The
  in-process engine (SQLite, and PostgreSQL's re-check of the targets) computed in `decimal`: a stored number such
  as `1e30` was reported as `NonNumericIncrementException` although it is a number, a sum beyond ±7.9e28 threw a
  raw `OverflowException` — on **PostgreSQL too**, where the `UPDATE` had succeeded and the exception came
  from the re-check afterwards — and a sum or stored number with more than 28 significant digits was silently
  rounded. SQLite now throws the new `PatchValueOutOfRangeException` (`DocumentType`, `DocumentId`, `FieldPath`)
  for all of these and rolls the patch back; PostgreSQL computes in `numeric` and simply succeeds, no exception.
  That divergence between the engines at the extremes stays documented (docs/spec/patch.md §5); the cases are
  pinned in `docs/spec/vectors/patch.json` with `knownDivergence` for PostgreSQL. **A SQLite patch that
  incremented such a number now throws `PatchValueOutOfRangeException` (it used to throw
  `NonNumericIncrementException` or `OverflowException`, or round).**
- **`CanonicalJson` no longer turns hand-built `JsonValue`s into `null` or fails on them (review C-6).**
  `JsonValue.Create(guid)` threw `InvalidOperationException`, `JsonValue.Create(poco)` was canonicalized — and
  hashed by the `Hash` policy — as `null`, silently. Such nodes are canonicalized by what they serialize to now; a
  value with no JSON form throws `CanonicalJsonException` (code `invalid-value`). Nodes produced by parsing or by
  the kernel are unaffected.
- **`DocumentDiff.FromJson` rejects malformed wire data with `ArgumentException` (review C-7).** A `ref` that
  was not a string, a `hash` that was a number or object, or an empty `ref` surfaced as
  `InvalidOperationException` or `ArgumentNullException` from deep inside; all are `ArgumentException(…, "json")`
  now. The wire format is unchanged, in particular a hashed entry without a hash is still written
  `{"changed": true}`.
- **GDPR erasure paths are validated before anything is rewritten (review C-15).** `RedactHistoryAsync` and
  `RedactEventsAsync` split their paths while looping over the rows: a malformed path (`a\x`, a trailing `\`)
  was skipped silently when no entry could be compared with it — the call reported 0 rewritten rows — or threw
  only after earlier rows had been prepared. Every path is checked first and a malformed one throws
  `ArgumentException` (parameter `paths`) before any row is touched. (Nothing was ever persisted half-way: the
  exception came before the `UPDATE`s.)
- **Failure-table error text never ends in half a surrogate pair (review C-12).** `last_error` was cut at 200 UTF-16
  units; an emoji at the boundary left a lone surrogate, which is not valid text for the database. The cut moves
  one unit back instead.
- **Argument exceptions name the parameter the caller passed (review C-11, Q-4).**
  `ChangeFeedProcessorOptions` reported a too-small `MaxRetryDelay` as a `BaseRetryDelay` error;
  `.Named("…")` on a document or event type blamed `documentType`; a property expression that is not a property
  chain (`Property`, `UniqueKey`, `LookupKey`, `PatchBuilder.Set/Remove/Increment`, `FieldChanged`) blamed
  `propertyExpression`, and the key lookups (`LoadByKeyAsync` & co.) blamed `keyExpression`. They report `MaxRetryDelay`, `name`, `property`
  and `key`. Only `ArgumentException.ParamName` changes; the exception types do not.
- **The GDPR export is one consistent snapshot (review S-2).** `GdprExport.ExportAsync` documented a "consistent
  snapshot" but ran its N+1 queries under READ COMMITTED, each with a snapshot of its own — an export could show
  document version 5 next to history up to version 6 when a writer committed in between (reproduced). It runs in a
  `REPEATABLE READ`, `READ ONLY` transaction now.
- **Patch errors are recognized without the server's message language (review S-9).** A non-numeric `Increment` and a
  patch path walking into an array were told apart from other `22P02` errors by English words in the message
  (`"type numeric"`, `"path element"`); with `lc_messages` other than English the caller got a raw `PostgresException`
  instead of `NonNumericIncrementException` / `PatchTargetMissingException`. The translation keys on the server
  routine that raised the error (`numeric_in`, `setPathArray`) now; a test pins both against the real server.
  Not tested against a translated server: the test image (`postgres:18-alpine`) ships no message catalogs.
- **Parallel first starts no longer fail, and a restart no longer locks the kernel tables (review S-11, N-8).**
  `SchemaManager.EnsureSchemaAsync` on an empty database from several instances at once failed with
  `23505 duplicate key … pg_namespace_nspname_index` (reproduced); and every start — the schema script is full of
  `ALTER TABLE …` — took `ACCESS EXCLUSIVE` locks on `papuma.document`, `change` and `event`, queueing behind any open
  transaction and, behind that queued lock, all traffic of a rolling deploy (reproduced: an unchanged restart blocked
  for as long as a reader's transaction stayed open). Starts are serialized by `pg_advisory_xact_lock`, and the DDL runs
  only when it is new: the kernel script and the key indexes of a model are recorded by content hash in the new table
  `papuma.schema_state` (created by the script; one row per applied hash, so several models sharing a database never
  mask each other and a changed model or a newer kernel applies its DDL). **Behavior change:** an object of the
  kernel schema that someone dropped by hand is no longer re-created at the next start — delete the rows of
  `papuma.schema_state` to force a re-apply. The key DDL is built (and validated) before the database is touched, so
  an invalid model now fails without any DDL. `ISchemaContributor`s are unaffected and keep their own lock.
- **One row with a foreign tenant id no longer blocks cross-tenant maintenance (review S-14).** The startup repair after
  a logical restore and event retention read the `(scope, tenant_id)` pairs of the affected rows and built a
  `ScopeContext` for each; a row written past the kernel with a tenant id outside `[A-Za-z0-9][A-Za-z0-9_-]{1,100}`
  threw `ArgumentException` and stopped the repair (so the host) and the retention for every tenant. Such rows are
  skipped now and logged as a warning (`EventRetention` and the hosted schema step log; the public entry points
  without a logger skip silently). They stay in place — no scope can delete or repair them.
- **Smaller fixes in the PostgreSQL store (review S-20).** A hash-key check row that is neither stored nor readable
  failed with a bare `ArgumentNullException`; it is an `InvalidOperationException` with an explanation now. The patch
  error filter translates once instead of twice; the event append, correlation lookup and event redaction bind their
  scope parameters through the shared helper.

- **A shutdown in the middle of a batch keeps the progress made (review P-3).** The feed engines threw away the
  position of the records a handler had already finished when the host stopped mid-batch: the next start delivered up
  to `BatchSize - 1` of them again, which shows for effect handlers (mail, webhook). On PostgreSQL the checkpoint
  (and the failure entries cleared on the way) is now committed with a non-cancellable token before the
  `OperationCanceledException` propagates; on SQLite the outcome phase runs although the handler phase ended in a
  cancellation. The record a handler was cancelled on is not counted and is delivered again, the ones before it
  are not. Best effort: if the cancellation tears the PostgreSQL connection down, nothing is persisted and the
  records come again (at-least-once, as before). A handler that has returned also finishes clearing its failure
  entry even if the shutdown arrives at that moment.
- **A handler waiting for its retry no longer maps a whole batch per poll cycle (review P-15).** The PostgreSQL
  engine loaded and parsed up to `BatchSize` rows every cycle while the first record sat out its backoff, only to
  discard them. Reading now stops at the record whose retry is pending (it and everything behind it were never
  delivered). Delivery is unchanged: poison records before it are still skipped, nothing behind it is delivered. A
  row behind a pending retry that cannot be mapped is no longer reported until the retry is due.
- **`PapumaKernelOptions` / `PapumaKernelLocalOptions` validate more at startup (review P-11).** A blank
  `ConnectionString` (`""`, whitespace) throws `ArgumentException` (`ConnectionString`) instead of failing at the
  first connection, and a `RetentionInterval` above what a `PeriodicTimer` accepts (about 49.7 days) throws
  `InvalidOperationException` instead of failing when the retention worker starts.

Breaking:

- **MCP tools: record lists come back as `{ "records": [...], "truncated": false }` and are capped (review M-3).**
  `get_document_history` and `get_changes_by_correlation` used to return a bare JSON array of any size — one
  injected "read the whole history" could flood the agent's context and the server's memory. They now return an
  object, at most `PapumaMcpOptions.MaxRecordsPerCall` records (new, default 500, must be > 0), and say whether more
  exist; the database read itself is limited (`LIMIT n+1`). `get_feed_failures` caps each feed and adds a top-level
  `"truncated"`. **A client that parses the history or correlation result as an array has to read `records`.**
- **MCP tools: what an agent can act on is now an `McpException` (review M-4).** An invalid `tenantId`, an unknown
  or non-exposed document type, a bad feed name, an unknown handler and disabled mutations used to throw
  `ArgumentException`/`InvalidOperationException`, which the MCP SDK reduces to "An error occurred invoking
  '<tool>'" — the carefully worded hint never reached the agent, which retried blindly. These cases now arrive
  with their message; all other exceptions stay generic on the wire. Code that called the tool methods directly and
  caught the old exception types has to catch `McpException`. Checked with a real MCP client/server pair.

- **Model checks for declarations that did nothing (review C-16, C-19, C-20, N-9).** `Build()` and the fluent API
  now refuse what used to be dropped silently: more than one policy attribute on a property (the undocumented
  precedence let `[TrackHash]` win over `[SensitiveData]` — a hash allows equality correlation, so it was not
  the stronger protection); a fluent policy or key on a `[JsonIgnore]` property (`ArgumentException`, it has no JSON
  path); a `[UniqueKey]`/`[LookupKey]` attribute on an event property (events have no keys). A fluent path through a
  `Nullable<struct>` (`x => x.Maybe!.Value.Street`) no longer yields the phantom path `maybe.value.street` that
  matched nothing. Fluent configuration still overrides attributes.
- **Feed handler names are validated (review P-6, Q-3).** A handler's `Name` is its identity in the shared
  `checkpoint` and `failure` tables, where the event feed stores its handlers as `event:<name>` — and nothing
  checked it: a change handler called `event:orders` shared one cursor and one failure list with the event
  handler `orders` (lost or doubled deliveries), a `null` name failed with a `ParamName` of `key`, an empty or
  whitespace name was accepted. All four processors (`ChangeFeedProcessor`, `EventFeedProcessor` and the SQLite
  twins) now throw `ArgumentException` (`handlers`) at construction for a name that is missing, longer than
  100 characters, or does not match `[A-Za-z0-9][A-Za-z0-9._-]*` — so `:` is out. **Upgrade:** every name in
  the samples, the docs and this repository already complies; a name outside the rule must be renamed, and
  because the name is the checkpoint identity, rename it together with its `checkpoint`/`failure` rows (or
  accept a replay).
- **The `Hash` policy is keyed (ADR-025): a model with `[TrackHash]` / `StoreAsHash()` needs a hash
  key, or the kernel refuses to start.** The old hash was an unsalted SHA-256 of the serializer's JSON
  text — neither canonical nor safe for low-entropy values such as phone numbers. It is now
  `v2:<kid>:<hex>`, an HMAC-SHA-256 over canonical JSON under a key derived per tenant and type.
  `DocumentStore` / `SqliteDocumentStore` throw `InvalidOperationException` (naming the fields) when the
  model declares a Hash policy and no `IHashKeyProvider` is given, and the hosted startup does the same
  before touching the database. v1 hashes already in the feed stay as they are — **read-only**, never
  written again, and they never match a v2 hash of the same value.

  *Upgrade:* (1) generate a key once — `openssl rand -base64 32` — and keep it in your secret store;
  (2) set `HashMasterKey` and `HashKeyId` on `PapumaKernelOptions` / `PapumaKernelLocalOptions` of
  **every** process that writes (or register your own `IHashKeyProvider`); (3) run the schema step once
  so `papuma.hash_key_check` exists, and let the application role `SELECT`/`INSERT` on it; (4) in tests,
  use `Papuma.Kernel.Testing.TestHashKeys` (`PapumaTestDatabase.CreateStoreAsync` already does). A
  consumer that compared hashes across records must now compare only hashes of the same form. To rotate,
  configure a new `HashKeyId` with the new key; a wrong key under an existing id fails at startup with
  `HashKeyMismatchException`.

- **Document ids may not contain control characters (review C-10).** `InputValidator.ValidateDocumentId` — used by
  every load, save, delete, patch, history, masked-read and GDPR entry point of **both** the PostgreSQL and the SQLite
  kernel — accepted NUL, line breaks, tabs and the like; PostgreSQL refused NUL only on the wire, SQLite stored
  them, and such an id would reach logs and `{Type}/{id}/{path}` references raw. Ids containing a control character
  (`char.IsControl`: U+0000–U+001F, U+007F–U+009F) now throw `ArgumentException` up front. Any other text stays legal,
  `/` and non-ASCII included. **A document that was saved under such an id can no longer be addressed by it.**
- **The GDPR export leaves out identifiers of other people by default (review S-3).** The change and event `metadata`
  of an export carried everything the session wrote — **before:** `correlationId`, `actorId` (the clerk),
  `causationId`, `causationType`, `traceparent`, rollback markers, and for a redacted record the whole `redaction`
  block (`redactedAt`, `reason` — free text, `actorId`, `correlationId`, `paths`). That names or describes third
  parties, which Art. 15 (4) GDPR does not hand to the data subject. **After:** only `correlationId`, `isRollback`,
  `restoredVersion` and, of a redaction, `redactedAt`, `correlationId` and `paths`; everything else, including keys a
  later version adds, stays out. An export consumer that read `actorId`, `causationId`, `traceparent` or
  `redaction.reason` from it no longer finds them: pass the new overload
  `ExportAsync(store, scope, documents, events, new GdprExportOptions { IncludeActorMetadata = true })` for the
  complete metadata (an internal audit export, not for the data subject). The existing overload is unchanged in
  signature and now means the reduced export. Document state, diffs and event payloads are untouched.
- **`RedactHistoryAsync` rejects an empty `paths` collection (review S-4).** `null` means "every entry of every change";
  an empty collection redacted nothing and returned 0, indistinguishable from a successful erasure that found
  nothing. It throws `ArgumentException` (`paths`) now, on both engines, like `RedactEventsAsync` always did.

- **`PapumaTestDatabase.ConnectAsync` refuses a role it must not reset (review P-17).** It resets an existing
  application role to `NOSUPERUSER` with a new password — which for `postgres` (or any superuser / `CREATEROLE`
  role) locked the account out of its own server. It now throws `ArgumentException` (`appRoleName`) before touching
  a role that is the connecting account, a superuser, or may create roles. Use a dedicated role name; ordinary
  existing roles are reused as before.

Changed:

- **One feed engine per backend instead of four copies (review Q-1, P-7, L-15).** `ChangeFeedProcessor`,
  `EventFeedProcessor`, `SqliteChangeFeedProcessor` and `SqliteEventFeedProcessor` were four ~700-line copies of
  one engine that had already drifted apart. Each backend now has one internal engine (`PostgresFeedEngine`,
  `SqliteFeedEngine`) that a feed extends with its table, checkpoint-key prefix and row mapping; the four public
  processors are thin facades with an **unchanged public surface** (pinned by reflection tests). Behaviour is the
  same, with the drift removed: the event feeds' raw `ResetCheckpointAsync` now logs like the change feeds'; the
  poison, failure and lifecycle log messages carry the feed as a structured value (`{Kind}`/`{Feed}`) instead of
  differing wording ("Event handler … skips poison event seq" → "Handler … skips poison event seq"). Anyone
  alerting on the exact text of those log lines has to adjust. The tests that were missing for the event feeds
  (poison, retry, lag, raw reset, leader coordination on PostgreSQL) were added first and ran green against the old
  code before anything moved.

- **F# wrapper: `runSession` no longer blocks, and the writes can be cancelled (review F-2, F-1).** `runSession`
  disposed the session with `GetAwaiter().GetResult()` inside a `finally` — a blocked thread per call and a
  deadlock under a single-threaded `SynchronizationContext` (UI thread, `.Local` on the desktop); a throwing dispose
  also replaced the exception of the body. The dispose is awaited now (the body's outcome is buffered and replayed
  with its original stack trace); if both throw, the caller sees the body's exception and the dispose failure
  hangs on it as `Data["Papuma.DisposeException"]`. New token variants — `trySaveWithTokenAsync`,
  `tryPatchWithTokenAsync`, `runSessionCommittedWithToken` — take the `CancellationToken` as the first argument
  (F# module functions have no optional parameters); the existing functions keep their signatures and pass
  `CancellationToken.None`. Cancellation surfaces as `OperationCanceledException`, not as a `KernelError`.

- **MCP: hardening without a design change (review M-8, M-5, M-6, M-7).** `get_feed_failures` cuts `lastError` at
  300 characters like the dashboard; the descriptions of all tools that return tenant data say that the values are
  untrusted and must not be followed as instructions. The reflection bridge to `GetHistoryAsync<T>` is replaced by
  an internal non-generic entry point in `Papuma.Kernel`. The `feed` argument errors name the right parameter.
  `WithPapumaKernel` rejects a `configure` callback that returns `null` (`ArgumentNullException`) and a second
  registration on the same service collection (`InvalidOperationException`). The class documentation no longer
  claims that every data tool is scope-bound: `get_feed_lag`/`get_feed_failures` span all scopes, and the document
  tools use the `tenantId` as given (binding it to the MCP caller stays the application's job, ADR-016).

- **Empty object keys are supported.** A dictionary key `""` (or `[JsonPropertyName("")]`) was rejected with
  `NotSupportedException` when a diff was computed. The path format already names it unambiguously as an empty
  segment — `""` at the top, `a.` below `a`, `.` below the empty key — so diff, `Apply`/`ApplyReverse`, policy
  resolution and GDPR paths now accept it (spec changes.md §2.1, vectors `empty-key-*`; the diff schema's path
  pattern allows empty segments). Consumers that parse paths must not assume non-empty segments.

- **Duplicate keys are rejected, by specification.** A document must not contain the same key twice in one object
  (spec changes.md §2.5); the engines fail instead of picking a value. PostgreSQL `jsonb` collapses duplicates
  (last wins) on write, so stored documents never contain them.

- **Samples:** the NATS bridge moved out of `shop-minimal-api` into its own worker sample
  `shop-nats-bridge` (same database, separate process). The shop no longer depends on `NATS.Net`.
- **`ScopeDataSourceFactory` is `[Obsolete]` (review S-13).** Nothing in the kernel uses it, it has no tests, and it is
  unsafe under load: LRU eviction disposes a data source a caller may still hold (`ObjectDisposedException`), and a call
  after `Dispose` creates data sources nobody disposes. Create and own one `NpgsqlDataSource` per tenant database
  instead. A warning only (CS0618); removal in the next major version. `IScopeDataSourceFactory` stays.
- **Guard clauses of the PostgreSQL session name the right argument (review S-10).** `PatchAsync` rejects an
  `expectedVersion` below 1 with `ArgumentOutOfRangeException` (it used to fail later as a version conflict or
  "not found"); `PatchWhereAsync` and `DeleteWhereAsync` reject a `null` value with `ArgumentNullException` (`value`)
  before a transaction is opened (it was an `ArgumentException` after); `GetHistoryAsync` reports `toVersion` when it
  is the bound that is wrong (it always blamed `fromVersion`).
- **Documentation corrected and extended (review S-8, S-15, S-16, S-17, S-18).** Actor ids must be pseudonyms — the kernel
  cannot redact them (ADR-017, `docs/gdpr.md`); a key lookup on a policy-protected field is an existence oracle
  (ADR-016 §5); `SET LOCAL`/`set_config(…, true)` outside a transaction is lost, not leaked — RLS fails closed
  (`ScopeConnectionExtensions`, pinned by a test); superusers, `BYPASSRLS` roles *and the table owner* are not bound by
  RLS — run the application as a separate, non-owner role (`SECURITY.md`, `SchemaDdl`, getting started);
  `idle_in_transaction_session_timeout` for sessions that only read (architecture).

- **Documentation corrected (review P-5, P-9, P-18).** `RetryFailureAsync` (all four processors) does not redeliver
  a poison record — the checkpoint is past it, only its failure entry is removed; the XML docs and
  `docs/observability.md` now say so, and name the working route (remove the entry, then rebuild the projection).
  Rejecting poison entries was considered and dropped: it would remove the only way to replay them. `IChangeHandler`
  and `IEventHandler` state that handlers run across all scopes and must establish the record's scope themselves.
  `docs/observability.md` recommends Npgsql's `Keepalive` so a half-dead `LISTEN` connection is noticed.

Added:

- **`PapumaTestDatabase.DropRoleOnDispose`** (default `false`, review P-20): removes the application role (and its
  privileges, `DROP OWNED BY`) on dispose when `ConnectAsync` created it, so test runs on a long-lived server stop
  accumulating roles. A role that existed before is left alone; with `StartAsync` the container takes the role
  with it, so the option does nothing there.
- **`DocumentTooDeepException`** (`Papuma.Kernel.Store`, derives from `NotSupportedException`): the one exception
  for a document nested deeper than 64 levels, with `DocumentType`/`DocumentId` when known. Saves, patches (a
  result too deep, or a value too deep on its own), reads of a stored state, the engines and the UTF-8 prototype
  throw it instead of a serializer `JsonException` that spoke of a "possible object cycle", a parser
  `JsonException` with a byte position, or a bare `NotSupportedException`. A reference cycle in a saved object is
  reported the same way (the serializer can only tell it by its depth). A failing write changes nothing — verified
  for patches on both engines. `CanonicalJson` keeps its own `CanonicalJsonException` code `too-deep`.

- Evaluation (internal, not used by the sessions): `Utf8JsonDiffEngine`, the diff computed on UTF-8 text without
  a node tree, with differential tests against `JsonDiffEngine`, PostgreSQL `jsonb` integration tests and
  end-to-end pipeline benchmarks (`-- diffpipeline`, `diffmemory`, `diffcold`). Results and the open semantic
  decisions (empty keys, depth limit, duplicate keys): `docs/analyses/utf8-diff-prototype-validation.md`.

- Test coverage: concurrent writers (optimistic versions and unconditional increments on one document) lose no update and leave no gap in the version numbers, on both engines. `concepts.md` now states that rollback never restores `DoNotTrack` fields.

- **`NonNumericIncrementException`** and **`PatchTargetMissingException`** (`Papuma.Kernel.Core`):
  typed patch failures (see Fixed). They carry `DocumentType`, and `DocumentId`/`FieldPath` when the
  engine knows them.

- **Hash key check (ADR-025).** A new table `papuma.hash_key_check` (`hash_key_check` on
  SQLite) stores one check value per hash key id — an HMAC of a fixed message, never the key. With a
  key configured, the hosted startup records it on the first start and verifies it on every later
  one (`SchemaManager.EnsureHashKeyAsync` / `SqliteSchemaManager.EnsureHashKeyAsync`, also callable
  without a host): an instance configured with a different key under the same `HashKeyId` fails to
  start with the new `HashKeyMismatchException` instead of writing hashes that match nothing.
  Changing the key means configuring a new `HashKeyId`. The check runs even when `EnsureSchema` is
  off, so run `EnsureSchema` once with this version first (the application role needs `SELECT` and
  `INSERT` on the new table).
- **`Papuma.Kernel.Testing.TestHashKeys`**: a published test hash key (`Provider`, `MasterKeyBase64`,
  `KeyId`); `PapumaTestDatabase.CreateStoreAsync` uses it. Never a real key.
- **Explicit logical type names (ADR-028).** `[DocumentType("customer")]` / `[EventType("user_signed_in")]` on a
  class, or `Named("…")` in the fluent model (which wins), set the name stored in every row's `document_type` /
  `event_type` — and with it the key index names, `Reference` text and the hash key of the type. **The default is
  unchanged** (the simple class name), so no existing model or row changes. Declare a name for any type that must
  survive being renamed or moved: with the class name as the name, renaming the class orphaned its documents
  (loads found nothing, silently). The pattern is unchanged (`[A-Za-z][A-Za-z0-9_]{1,100}`); the uniqueness error
  now names the fix.
- **Keyed `Hash` policy (ADR-025).** `IHashKeyProvider`, the default
  `MasterKeyHashKeyProvider` (HKDF-derived key per tenant and type), the options `HashMasterKey`
  (base64, at least 32 bytes) and `HashKeyId` on `PapumaKernelOptions` and `PapumaKernelLocalOptions`
  (validated at startup), and store constructors that take a provider. With a key configured,
  `[TrackHash]` fields — in feed diffs, policy-projected reads and event payloads — carry
  `v2:<kid>:<hex>`, an HMAC-SHA-256 over canonical JSON (`docs/spec/canonical-json.md`); a value
  without a canonical form (a number longer than 1024 characters, an unpaired surrogate) fails the
  write with the new `CanonicalJsonException`. A provider registered in the container before
  `AddPapumaKernel` wins over the options. (The key is mandatory for models with a Hash policy, see Breaking.)
- **`LoadAllByKeyAsync`** on both sessions (ADR-026): all documents behind a declared key — single-field
  or composite, unique or lookup — in ascending ordinal `id` order, paged with a required `limit`
  (1 to `KeyMetadata.MaxLoadAllLimit`, 1000) and `afterId`. Scope-bound, transactional and clear-text like
  `LoadByKeyAsync`; backed by the declared key's index on both engines (pinned by `EXPLAIN` tests). Closes
  the gap that a `LookupKey` had an index and no read that returned its matches.
- **Storage-neutral specification** in `docs/spec/` (diff, policies, patch) with language-neutral
  conformance vectors that run against PostgreSQL and SQLite. Repository only for now.
- **`RunFeedWorkers`** on `PapumaKernelOptions` and `PapumaKernelLocalOptions` (default
  `true`; feedback F-23). `false` registers the feed processors without their hosted
  loops — schema setup, contributors and event retention still run — so a test fixture
  that boots the real host drives the feeds with `DrainAsync` (or `ProcessOnceAsync`)
  alone instead of racing the hosted worker for its checkpoints. Also for instances
  that should not process feeds. getting-started §7 shows the fixture.

Fixed:

- **Security: a policy on a nested field no longer leaks through an entry that carries the whole object.** A
  `[SensitiveData]` (or `[TrackHash]`, `[TrackReference]`, `[DoNotTrack]`) property inside a nested object —
  `address.street` — was stored in clear under its container's path (`address`) on insert, on delete, when the
  object was set or cleared, and on any type change to or from an object, because policy resolution only looked
  at the entry's path and its ancestors. Such entries are now split before the policies apply: the container
  keeps the rest of its value, each subtree with a different effective policy gets its own entry (spec
  changes.md §3, *Policies below an entry*; vectors `nested-policy-*`). A stored diff can now hold a path and
  paths below it. `Apply`/`ApplyReverse` no longer create a missing parent when removing a field, which keeps such
  diffs reversible. Diffs written before this fix are not rewritten; `RedactHistoryAsync` reaches them (next entry).

- **`RedactHistoryAsync` erases a nested path inside whole-object entries.** An erasure of `address.street` only
  rewrote entries at or below that path, so the street stayed in every entry that carried the whole `address`
  object — every insert, delete and set or cleared object written before the fix above, and still today for a
  nested field without a policy. An ancestor entry is now redacted when its old or new value holds the erased
  path; a split container that no longer holds it is left alone. Both engines.

- **The diff, canonical and projection engines refuse a document nested deeper than 64 levels** with a clean
  exception instead of recursing into it — for a hand-built `JsonNode` graph, where a recursive walk would end in
  an uncatchable stack overflow. 64 is also what the parser allows and, with `KernelJson.Options.MaxDepth = 65`
  (the serializer writes one level less than its setting), exactly what a session can save: a 64-level document
  round-trips, the 65th level is refused. (An earlier draft of this entry allowed 128 levels in the engines,
  which no stored document could reach.)

Docs:

- **One Papuma per database** is now stated where it matters (getting-started §2, architecture §4, the
  playbook's hard rules and the agent snippet): the `papuma` schema, the `papuma_changes` NOTIFY channel
  and the checkpoints are fixed per database, so each application gets its own database (one server can
  host many); own tables go in a schema of their own.
- **Recipe `backup-restore`**: what to back up, `pg_dump`/`pg_basebackup`, the restore
  checklist (cluster-bound transaction ids, external targets, erased data coming back),
  and replication — physical streaming replication yes, logical no, never read from a
  standby, poolers. Verified by a real dump/restore between two clusters and by an incremental
  `pg_basebackup` chain merged with `pg_combinebackup`.

Changes:

- **Docs: "at most one per …"** (feedback F-19) — concepts §34 and the playbook show the
  slot document (an id derived from what it is unique for, inserted first with
  `expectedVersion: 0`) as the way to express uniqueness among documents in a state.
  Conditional keys are deferred against a trigger (ADR-020, amended).
- **Docs: marking an existing projection and changing its shape in one release**
  (feedback F-22) — the projection-schema recipe shows the one-deploy routes: a new
  name, or `ResetProjectionAsync(name)` from the contributor's guarded breaking DDL step.
  The first-marking rule (record the version, no rebuild) is unchanged.
- **Tooling: `publish-nuget.sh --tag vX.Y.Z`** packs the tagged commit in a temporary
  worktree instead of the working tree (which may be ahead of the tag — the docs ship
  inside the packages), and refuses to upload unless every package carries the tag's
  version. Without `--tag` it warns when HEAD is not a tagged commit or has uncommitted
  changes. The output path is passed to `dotnet` natively on Git for Windows.

## 2.1.0 (2026-09-28)

The feedback round from aksara (F-16 to F-21) and a row-level security fix. **Every
PostgreSQL user of 2.0.x should upgrade** — in 2.0.x the `All` scope could delete rows of
every tenant, and event retention deleted nothing under RLS. Source-compatible; the
schema migrates itself at startup. Add the `DELETE` policy below to your own RLS tables.

Upgrade notes — what a consumer of 2.0.x can notice:

- **A session disposed with uncommitted writes now logs a warning** (event id 1001,
  `UncommittedSessionDisposed`) and counts `papuma.session.uncommitted_disposals`. The
  rollback itself is unchanged. Where a rollback is intended, call `DiscardAsync()` first;
  a dispose on an exception path still logs — next to the exception.
- **Projections declare themselves** (`IProjection`, ADR-024) — optional, nothing changes
  for unmarked handlers. When you mark an *existing* projection, its first start only
  records the declared `Version`: it does **not** rebuild, or the upgrade would rebuild
  everything. To rebuild, raise the version or call `ResetProjectionsAsync()`.
- **The MCP tool `reset_feed_checkpoint` only resets projections now** — mark the
  handlers you reset through it with `IProjection`; it empties their target
  (`ResetAsync`) before the replay, and refuses effect handlers.
- **`papuma.checkpoint` gains `projection_version`** (both kernels), added at startup.
- **Add a `DELETE` policy to your own RLS tables** (PostgreSQL). The ADR-019 template —
  `USING (scope_visible) WITH CHECK (scope_writable)` — let the `All` scope delete every
  tenant's rows, because PostgreSQL checks a `DELETE` against `USING` only. Add to each
  table built from it:
  `CREATE POLICY scope_delete ON <table> AS RESTRICTIVE FOR DELETE USING (papuma.scope_writable(scope, tenant_id));`
  The kernel's own tables get it from `EnsureSchemaAsync`.

Changes:

- **New: a forgotten `CommitAsync` is no longer silent** (feedback F-18). Both kernels'
  sessions log and count a dispose with uncommitted writes, naming the correlation id,
  the number of writes and the first of them. `DocumentSession.DiscardAsync()` /
  `SqliteDocumentSession.DiscardAsync()` roll back on purpose and keep the session
  usable. The stores take an optional `ILogger` (new constructor overloads;
  `AddPapumaKernel` / `AddPapumaKernelLocal` pass it from DI). F#: `runSessionCommitted`
  commits when the body returns `Ok` and discards on `Error`. A throwing strict mode was
  left out on purpose: a dispose on an exception path would replace the original
  exception.
- **New: `NpgsqlDataSource.OpenScopedAsync(scope)`** (feedback F-21) — a connection with
  an open transaction whose scope is already set, for application tables under RLS
  (ADR-019). Its `CreateCommand()` binds every command to that transaction, so none runs
  outside the scope by accident; disposing without `CommitAsync` rolls back. Replaces
  the four-line open/begin/`SetScopeAsync`/`cmd.Transaction` sequence at every read and
  projection site; the same-database recipe uses it.
- **Docs: append-only projections** (feedback F-20) — `projection-schema.md` §3 keys the
  rows by the change's `seq` (`ON CONFLICT (seq) DO NOTHING`), so redelivery and rebuild
  add nothing twice and a reset needs no `TRUNCATE`; reads order by `seq`, which is the
  same live and after a rebuild; pruning to the newest N stays correct.
- **New: `LoadManyAsync<T>(ids)`** on both kernels' sessions (feedback F-17, part 2) —
  several documents of one type by id in one query, scope-bound, including the session's
  own uncommitted writes; missing ids are absent from the result, duplicates load once.
  The batch handler interface from the same entry is deferred until a measurement shows
  the per-change reload to be the bottleneck.
- **`Papuma.Kernel.Testing`: `GrantAppRoleAsync(schema)` also grants `TRUNCATE`** on
  application schemas — the reset of a projection under row-level security — but never
  on `papuma`, where `TRUNCATE` would bypass RLS.
- **New: projections and effects are declared** (ADR-024, feedback F-16). A projection
  implements `IProjection` — a `Version` and an idempotent `ResetAsync` that empties its
  target. A raised version rebuilds it once at the next start, under the same name;
  instances still running older code in a rolling deploy pause it instead of writing the
  old shape. `ResetProjectionsAsync()` rebuilds all projections of a processor,
  `ResetProjectionAsync(name)` one. `[StartsAtFeedHead]` makes an effect handler's first
  start skip the history. Lag snapshots carry `ProjectionVersion` and `Paused`; the
  dashboard and `get_feed_lag` show each handler's kind. Both kernels, both feeds.
- **Fixed (PostgreSQL, security): the `All` scope could delete** rows of every tenant in
  `papuma.document`, `papuma.change` and `papuma.event` (and in application tables built
  from the ADR-019 template): a `DELETE` is checked against the policy's `USING` only.
  A restrictive `DELETE` policy now requires a writable scope; `All` reads and never
  writes, as ADR-019 always stated (amended).
- **Fixed (PostgreSQL): event retention deleted nothing under row-level security.** The
  purge set no scope, so a role subject to RLS — the application login, even as table
  owner under `FORCE ROW LEVEL SECURITY` — saw no rows. It now finds the affected tenants
  under `All` and deletes under each tenant's scope. Its test ran as a superuser, which
  RLS does not filter; it now runs as a plain login role.
- **Fixed (PostgreSQL): the repair after a logical restore (2.0.0) failed under RLS** —
  its `UPDATE` ran under `All`, which `WITH CHECK` rejects, so `EnsureSchemaAsync` would
  have stopped the application's start after a `pg_restore`. It now writes tenant by
  tenant; tested as a plain login role.

## 2.0.0 (2026-09-27)

A major version for one reason: the feed's ordering contract changes (causal order
instead of global `seq` order) and direct-SQL consumers must switch queries. The API
is source-compatible; the schema migrates itself at startup. **Every PostgreSQL user
of 1.x should upgrade** — the 1.x feeds can skip records.

Upgrade notes — what a consumer of 1.4.x can notice:

- **Rebuild your projections after upgrading.** Up to 1.4 the PostgreSQL feeds could
  skip changes and events for good (see *Fixed* below). Upgrading stops new losses; it
  cannot deliver what was skipped before. Reset every handler whose output must be
  complete (`ResetCheckpointAsync`) and let it replay. Effect handlers (mails, webhooks)
  may have missed work — the records themselves are intact in `papuma.change` /
  `papuma.event`, so a one-off query can find what was never acted on.
- **Delivery order is causal order, not global `seq` order.** A handler receives a
  transaction's records after those of every transaction that committed before it
  started; concurrent transactions come in no guaranteed order; per document strictly
  by version. A lower `seq` can now arrive after a higher one. Handlers that use `seq`
  as a watermark ("skip everything ≤ the highest seen") drop records — use the
  documented idempotency (handler + `seq`, or the document version) instead. Handlers
  must not decide anything from the order of unrelated documents: live delivery and a
  rebuild may order concurrent transactions differently (concepts §2).
- **New `txid` indexes** on `papuma.change` and `papuma.event` (`ix_papuma_change_txid`,
  `ix_papuma_event_txid`) and four columns on `papuma.checkpoint` are created at the
  first startup by `EnsureSchemaAsync`; existing checkpoints are carried over. On large
  tables create the indexes beforehand without blocking writes:
  `CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_papuma_change_txid ON papuma.change (txid);`
  (and the same for `papuma.event`).
- **Direct-SQL feed consumers must switch queries.** The read prescribed by
  `feed-wire-format.md` up to 1.4 (`seq > checkpoint AND txid < xmin`) has the same
  defect. Section 4 now specifies the snapshot cursor; the Python and Go samples use it.
- **`ChangeFeedLagSnapshot.Lag` is a count** of committed, undelivered records;
  `Checkpoint` is the highest delivered `seq`. Open transactions are no longer lag.
- **After a logical restore** (`pg_dump`/`pg_restore` into another cluster)
  `EnsureSchemaAsync` repairs the feed state (transaction ids are per cluster); records
  delivered shortly before the dump may be delivered again (at-least-once).
  `Papuma.Kernel.Local` is unaffected by all of the above.

Changes:

- **Fixed (PostgreSQL): the change and event feeds could skip records of interleaved
  transactions.** The read predicate `seq > checkpoint AND txid < pg_snapshot_xmin(…)`
  assumed that an older transaction also draws its sequence numbers earlier. A session
  writing several times breaks that: A writes (seq 96), B writes (97), A writes again
  (98) and commits — the checkpoint moved to 98 while B was open, and B's 97 was never
  delivered, by any handler, without a failure entry (jejak feedback F-15). No predicate
  over a sequence checkpoint can fix this; a handler's position is now a transaction
  snapshot (ADR-022, the PgQ model): each cycle delivers the transactions committed
  since, as a slice in `seq` order. Proven by a concurrency stress test (interleaved
  multi-write sessions, random commit and rollback, two competing processors) that
  fails on the old engine on every run.
- **A long-open write transaction no longer stalls the feed.** Under the old horizon
  any open transaction held back every later commit; now it holds back only its own
  records.
- **New: `SchemaManager.RepairFeedAfterLogicalRestoreAsync`** — the restore repair that
  `EnsureSchemaAsync` runs, callable directly when the schema is managed elsewhere.
- **Docs:** ADR-022 (snapshot cursor; supersedes point 2 of ADR-010, amends ADR-009's
  ordering), concepts §2 rewritten, feed-wire-format §4 rewritten; ADR-023 (why there is
  no event-sourcing mode) with the stream-shaped aggregates recipe.

## 1.4.0 (2026-09-27)

Upgrade notes — what a consumer of 1.3.x can notice:

- **A new index on `papuma.change`** (`ix_papuma_change_correlation`) is created at the
  first startup by `EnsureSchemaAsync`. A plain `CREATE INDEX` blocks writes to the table
  while it builds; on a large change table, create it beforehand without blocking:
  `CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_papuma_change_correlation ON papuma.change
  (scope, tenant_id, (metadata ->> 'correlationId'));` — startup then finds it in place.
- **`Papuma.Kernel.Local` feed handlers now run with no transaction open.** Handlers that
  write to the same database file start working; nothing else changes for correct
  handlers. Run one feed processor per database file (the hosted service does).
- **`SqliteChangeNotifier.WaitAsync` is obsolete** — compiler warning; wait on
  `Subscribe()` instead.
- **Retry timing on PostgreSQL follows the database clock.** Retries no longer shift by
  the clock skew between application and database hosts.

Changes:

- **Fixed (`Papuma.Kernel.Local`): handlers could not write to the database file.** The
  feed processors held a `BEGIN IMMEDIATE` transaction — the file's single write lock —
  while handlers ran, so a local projection writing through its own connection got
  `database is locked` (then retry, then poison), and any slow handler (mail, HTTP)
  blocked every application write for its duration. Both processors now read a batch
  in a short transaction, run the handlers with no transaction open, and record the
  outcome in a second short one — only if the checkpoint was not moved meanwhile (a
  reset for a rebuild wins; the stale outcome is dropped and re-read). At-least-once,
  ordering and stop-the-line are unchanged. The old lock also serialized a second
  processor instance on the same file; run one per file (the hosted service does).
- **Fixed (`Papuma.Kernel.Local`): the event feed missed wakeups.** Change and event
  processor shared one single-slot signal; whichever read it first took it, the other
  slept until the poll interval (default 5 s). `SqliteChangeNotifier.Subscribe()` gives
  every waiter its own buffered signal and a commit reaches all of them;
  `SqliteChangeNotifier.WaitAsync` is obsolete for that reason.
- **Fixed (PostgreSQL): retry backoff mixed two clocks.** `next_retry_at` was set with
  the database clock and compared with the application host's clock, shifting every
  retry by the clock skew between the two (a few ms in a container were enough to make
  zero-delay retries wait). The due check now runs in the database
  (`next_retry_at > now()`), for the change and the event feed.
- **`GetChangesByCorrelationAsync(correlationId)`** on both kernels' sessions: every
  change a unit of work produced, across document types, in feed order, scope-bound —
  "what did this command do?" for audit timelines and for tests asserting a command's
  complete effect. Backed by a new index on the change table's `correlationId`
  (created idempotently by `EnsureSchemaAsync`). jejak feedback F-12.
- **MCP: `get_changes_by_correlation`** — the correlation read as a read-only tool, so an
  agent can answer "what did this command do?" from any change's `correlationId`
  (policy-applied diffs, scope-bound, like `get_document_history`).
- **`ISchemaContributor` / `AddSchemaContributor<T>()`**: application schema
  (projection tables, their RLS policies) applied at startup after the kernel schema
  and before the feed workers — so policies can use the ADR-019 functions and handlers
  never run against a missing table. New recipe
  [schema for projection tables](docs/recipes/projection-schema.md): idempotent DDL,
  an advisory lock for concurrent starts, and breaking changes as a rebuild through a
  new handler name. Verified by `ProjectionSchemaTests`. jejak feedback F-11.
- **`Papuma.Kernel.Local`: schema contributors** — `AddSchemaContributor<T>()` with an
  `ISqliteSchemaContributor` (the kernel's open `SqliteConnection`), run after the
  kernel schema and before the feed workers, like the Postgres hook.
- **Write-path storage probe** (`dotnet run -c Release -- writepath` in
  `benchmarks/Papuma.Kernel.Benchmarks`): saves/s, p50/p95, WAL per save, HOT ratio and
  table/index growth for 5–50 KB incompressible documents, 0 vs 3 declared keys, 1/16/32
  sessions, patch and full save, plus `fillfactor` 90 and `lz4` TOAST compression — a
  fresh PostgreSQL 18 container per scenario, `--rounds`/`--filter` for repeat
  measurements. Result in concepts §14: declared keys take HOT from 92–100 % to 0 %, but
  at realistic shape that costs 0–9 % WAL and no measurable throughput — the trigger for
  the deferred key side table did not fire. jejak feedback F-14.
- **Docs: write-path storage costs** (concepts §14). Measured: one declared key anywhere
  in the model makes HOT updates impossible for every document (0 % vs 52–66 % without
  keys), because keys are expression indexes over the always-changing `data` column.
  Document size is now documented as a modelling cost (whole-document rewrite per save;
  `default_toast_compression = lz4` for large documents). A key side table and
  partitioning of `papuma.change` are deferred with measurable triggers; tenant-based
  distribution is noted as the long-range route. jejak feedback F-14.
- **Docs: the raw feed is an in-application contract.** concepts §21, the feed wire
  format, the playbook, getting-started and both factsheets now separate the stable
  *format* from the *content* (your documents' field paths) and point other teams and
  systems to explicit integration events at the boundary. jejak feedback F-13.
- **ADR-021 evidence log** records the first consumer data point for a testing-package
  xUnit adapter (jejak, F-10) and that the correlation read API it listed as a
  prerequisite now exists.

## 1.3.0 (2026-09-26)

Upgrade notes — what a consumer of 1.2.x can notice:

- **MCP over HTTP is stateless** (ModelContextProtocol 2.x): no `Mcp-Session-Id` is
  issued, and a request carrying one gets 400. Set `Stateless = false` on
  `WithHttpTransport` if a client needs sessions.
- **`SetQ`/`RemoveQ`/`IncrementQ` (F#) are obsolete** — compiler warnings, still
  working; switch to `p.Set((fun (x: T) -> x.Field), v)` and friends.
- **`KeyPath` of a composite key is comma-separated** (`projectId,number`) — code
  that parses `UniqueKeyViolationException.KeyPath` as one dot-path should use
  `KeyPaths`. Single-field keys are unchanged.
- **`ScopeContext.Tenant(null)` throws `ArgumentNullException`** naming `tenantId`;
  the accepted id pattern only widened.
- **Packaged docs moved** from `docs/vNEXT/` to `docs/`.

Changes:

- **New package `Papuma.Kernel.Testing` (ADR-021).** `PapumaTestDatabase` starts
  PostgreSQL 18 via Testcontainers (or connects to an existing server) and adds a
  login role without superuser or `BYPASSRLS`, so tests exercise row-level security as
  production does — a superuser connection passes even when isolation is broken.
  `CreateStoreAsync(model)` applies schema and grants; `GrantAppRoleAsync(schema)`
  covers application tables. `DrainAsync()` on `ChangeFeedProcessor` and
  `EventFeedProcessor` runs a feed to quiescence and throws `FeedDrainException` when a
  handler failed (a stopped feed otherwise just delivers nothing) or the feed does not
  settle. Test-framework agnostic; the kernel's own suite now runs on it. A broader
  package (framework adapters, change assertions) is deferred against objective
  triggers named in the ADR. getting-started §7 and concepts §32 describe the setup.
- **Composite keys (ADR-020).** `UniqueKey`/`LookupKey` accept an anonymous type —
  `.UniqueKey(x => new { x.ProjectId, x.Number })` — materialized as one multi-expression
  partial index on PostgreSQL and SQLite. New overload
  `LoadByKeyAsync<T>(key, IReadOnlyList<object> values)` looks up with one value per
  component. Like single-field keys, a composite key is enforced only for documents
  that carry every component. `PatchWhereAsync`, `DeleteWhereAsync` and masked reads by
  key stay single-field and reject composite keys. `KeyMetadata` gains `Paths`,
  `IsComposite` and `ComponentSegments`; `UniqueKeyViolationException` gains `KeyPaths`.
  For composite keys, `Path`/`KeyPath` hold the component paths comma-separated;
  single-field keys are unchanged. The kernel never drops indexes: replacing a
  flattened helper-field key leaves its old index until you drop it.
- **Row-level security for your own tables (ADR-019).** `EnsureSchemaAsync` now creates
  `papuma.scope_visible(scope, tenant_id)` and `papuma.scope_writable(scope, tenant_id)`
  — the kernel's scope rules as SQL functions, to be used in RLS policies on
  application tables (typically projection targets) in the same database. They are
  the public contract; the underlying setting names stay internal. A test pins them to
  the kernel's own policies. New recipe
  [read models in the same database](docs/recipes/same-database-read-models.md),
  verified by `ScopeFunctionTests`, and concepts §30.
- **`SaveResult.GetDocument<T>()`** returns the document as persisted by the write —
  in particular the value a `PatchAsync` `Increment` produced, without a second read and
  without the conflict window of load + save (sequence numbers, ticket keys). Unlike
  `Diff` it is not policy-applied and holds every field. Works for patch, save and
  rollback results in both kernels; throws after a delete or for the wrong type.
  concepts §17 gains a "sequence numbers" paragraph.
- **Tenant ids accept GUIDs.** `ScopeContext.Tenant` now accepts
  `^[A-Za-z0-9][A-Za-z0-9_-]{1,100}$` (was `^[A-Za-z][A-Za-z0-9_]{1,100}$`): a leading
  digit and `-` are allowed, so GUID strings work without a prefix. Every previously
  valid id stays valid. The pattern was a whitelist, never an SQL-safety measure —
  tenant ids only reach SQL as parameters. New overload `ScopeContext.Tenant(Guid)`
  yields the canonical lowercase dashed form (ids compare case-sensitively) and rejects
  `Guid.Empty`. A null id now throws `ArgumentNullException` naming `tenantId`.
- **F#: key declarations from lambdas work.** F# lowers `fun x -> box x.Email` to a
  call to `Operators.Box` instead of C#'s `Convert` node, so `UniqueKey`, `LookupKey`,
  `Property`, `LoadByKeyAsync` and the key-based bulk operations rejected every F#
  lambda; only `[<UniqueKey>]` attributes worked. The path resolver now unwraps F#'s
  `box` too. Composite keys from F# use a tuple (`box (x.ProjectId, x.Number)`);
  anonymous records are rejected, because F# sorts their fields. The F# README,
  playbook and snippet show the forms; F# tests cover them end to end.
- **F#: `SetQ`/`RemoveQ`/`IncrementQ` deprecated.** They rested on the premise that F#
  cannot turn lambdas into LINQ expressions; it can. The real obstacle was the
  unrecognized F# `box` (fixed above), so the kernel's own Patch API now works from F#:
  `p.Set((fun x -> x.Name), v).Remove(fun x -> box x.Note).Increment((fun x -> box x.Count), n)`.
  The quotation members still work, are marked `[<Obsolete>]` and are planned for
  removal in 2.0. Docs, the slice conventions and the `fsharp-local-todo` sample use
  the lambda form; concepts §29 records the correction. One F# habit comes with it:
  annotate the lambda parameter (`fun (x: TodoItem) -> x.Done`) unless a type argument
  fixes the document type — F# otherwise infers the most recently declared record type
  with that field name (running the sample caught exactly this).
- **Dependencies:** `ModelContextProtocol` 1.4 → 2.2 (`Papuma.Kernel.Mcp`, sample),
  `Microsoft.Extensions.*` 10.0.0 → 10.0.12, `Microsoft.Data.Sqlite` 10.0.10 → 10.0.12
  (the `SQLitePCLRaw` 3.0.5 pin stays), test tooling (Test SDK 18, xunit runner 4,
  coverlet 10), GitHub Actions (checkout v7, setup-dotnet v6, upload-artifact v7).
  **MCP over HTTP is now stateless by default** — no `Mcp-Session-Id`, and a request
  carrying one gets 400. The Papuma tools hold no session state; clients that need
  sessions set `Stateless = false` (observability.md). The sample's
  `diagnostics.http` drops the session-header handshake.
- **Packaging: one doc set in every package, with no dangling links.** All packages
  (`Papuma.Kernel`, `.Local`, `.FSharp`, `.AspNetCore`, `.Mcp`, and the new `.Testing`) now ship the same
  `docs/` set — guides, ADRs, recipes, the `ai/` set and `analyses/` — defined once in
  `Directory.Build.targets`. Before, each package shipped its own subset, and links
  between documents dangled depending on the package (e.g. `concepts.md` →
  `getting-started.md` inside `Papuma.Kernel.Local`). PostgreSQL-only pages now say so at
  the top and point `Papuma.Kernel.Local` users to the playbook's Differences section.
  Links to files outside the set (samples, `legacy/`, repo files) are absolute GitHub
  URLs; the package READMEs link only absolutely, since nuget.org resolves no relative
  link (this also fixes the logo there). A new test (`PackagedDocsLinkTests`) fails on
  any link or heading anchor that would dangle inside a package. The AGENTS snippet now
  tells agents to read the docs from the package folder of the version they build
  against.
- **Packaging**: packages now carry the project icon, a README, Source Link metadata
  (`PublishRepositoryUrl`, `EmbedUntrackedSources`, deterministic CI builds), XML
  documentation for IntelliSense, and a `.snupkg` symbol package.
- **Docs: consumer feedback from jejak.** `getting-started.md` gains §7 "Without a
  host" (hand-built store, `ChangeFeedProcessor.ProcessOnceAsync`; the integration-test
  part later moved to `Papuma.Kernel.Testing`) and documents the tenant id pattern and
  keys (both later extended — GUID tenant ids, composite keys; see above). concepts §17
  now states that `Increment` is safe against concurrency but not against duplicate
  commands. The playbook carries these points and no longer links to files that are
  not shipped in the `Papuma.Kernel` package.
- **Docs:** the AGENTS snippet names the RLS functions and the testing package in its
  rules; slice conventions and the event-modeling recipe point to
  `Papuma.Kernel.Testing`; `llms.txt` is current (32 concepts sections, 21 ADRs, the
  testing package, the shared doc set).
- **Docs reorganised for the public repository.** The `vNEXT` working title is gone:
  `docs/vNEXT/*` moved up to `docs/` (guides flat, `docs/adr/`, `docs/recipes/`), and
  every frozen pre-1.0 document — the German v1 implementation set, the three v1-era
  ADRs, the v1 change-feed DSL spec, the German `concepts.de.md`, the v1 assessment and
  polling analysis, and the 13-phase implementation plan — now lives under
  `docs/legacy/`, which states plainly that it is not maintained.
  [`docs/README.md`](docs/README.md) is the new map. **Links into `docs/vNEXT/` or
  `docs/v1/` no longer resolve**; the packaged doc set moved the same way
  (`docs/vNEXT/concepts.md` → `docs/concepts.md` inside the NuGet packages).
- **Dependencies**: `Testcontainers.PostgreSql` 4.1.0 → 4.15.0, which drops the
  transitively vulnerable `SSH.NET` 2024.1.0 (GHSA-q939-rpr3-3284). Test-only.
- **Build is warning-free again**: resolved XML `cref` references that broke once
  documentation generation was enabled, and the obsolete `PostgreSqlBuilder()` constructor.
- **Project files**: added `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`,
  issue/PR templates, Dependabot, `.editorconfig` and `.gitattributes`. The internal
  security/architecture review pair was removed from the repository root.

## 1.2.1 (2026-09-07)

- **Docs**: closed the same gap for F# that opened for `Papuma.Kernel.Local`
  in 1.1.0 — `llms.txt` and the `docs/ai/` set were entirely silent on
  `Papuma.Kernel.FSharp` and would have misled a coding agent building an F#
  app. Added, all additive (no restructuring, same pattern as the existing
  `Papuma.Kernel.Local`-differences sections):
  - Playbook: a "Using `Papuma.Kernel.FSharp`" section (`SetQ`/`RemoveQ`/
    `IncrementQ`, `trySaveAsync`/`tryPatchAsync`, `runSession`, the
    `CommitAsync` and no-DU/`option` rules).
  - AGENTS.md snippet: an F# addendum on top of either kernel's block.
  - Slice conventions: a full F# translation of the Command slice (the one
    that actually diverges — Patch needs quotations, minimal-API needs a
    `Func<_,_>` delegate wrapper), plus the Registration/Test idiom
    differences; View/Automation/Translation follow the same pattern.
  - `llms.txt`: points agents at all of the above and at the
    `Papuma.Kernel.FSharp` README.
  - `Papuma.Kernel.FSharp` now bundles the same `docs/ai/` set and
    `concepts.md` its sibling packages do, so the guidance is on disk after
    `dotnet restore` without needing to know to look in another package's
    folder.

## 1.2.0 (2026-09-05)

- **New package: `Papuma.Kernel.FSharp`** — an F#-idiomatic facade over the
  write path, additive on top of `Papuma.Kernel.Core` (no kernel change).
  Quotation-based `Patch` (`SetQ`/`RemoveQ`/`IncrementQ`) via a hand-rolled
  quotation-to-`Expression<Func<T,TValue>>` walker — `<@ fun x -> x.Field @>`
  converts directly, no `Func<_,_>` wrapper needed. `Result<'T, KernelError>`
  instead of exceptions for the three expected write outcomes ADR-003/006
  document (`VersionConflict`/`DocumentNotFound`/`UniqueKeyViolation`) via
  `trySaveAsync`/`tryPatchAsync` — written against statically resolved type
  parameters (SRTP) so one implementation covers both `Papuma.Kernel`
  (Postgres) and `Papuma.Kernel.Local` (SQLite), which share no common
  interface, only identical method shapes. `runSession` fills the
  `IAsyncDisposable` gap F#'s `use` doesn't cover. Deliberately does not
  support discriminated-union or `option` fields on document types — the
  kernel's JSON serializer config stays fixed on purpose (one deterministic
  wire format across every language and process); see
  `docs/concepts.md` §29 for the full reasoning and the model-at-the-
  boundary alternative.
- **New sample: `samples/fsharp-local-todo`** — the first sample against
  `Papuma.Kernel.Local` (the other three are Postgres-only), a minimal F#
  ASP.NET Core API exercising the full `Papuma.Kernel.FSharp` facade:
  `[<UniqueKey>]` on a record field, `SetQ`/`IncrementQ` in one `Patch` call,
  `trySaveAsync`/`tryPatchAsync` mapped to HTTP 409/404, `runSession`, and an
  F#-authored `IChangeHandler` on the feed.
- **`publish-nuget.sh` retargeted at GitHub Packages** (was hardcoded to a
  private Gitea instance) — the GitHub owner is now auto-detected from the
  `origin` remote instead of a hardcoded default, so the script works
  unchanged in any GitHub repo. Project discovery now also picks up
  `.fsproj` packages, not just `.csproj`.

## 1.1.0 (2026-08-11)

- **New package: `Papuma.Kernel.Local`** — a SQLite-backed sibling of
  `Papuma.Kernel` for single-writer desktop/embedded use, at full parity with
  the Postgres kernel's public API (Load/Save/Delete/versioning/diff/change
  feed, Patch and bulk operations, GDPR redaction, masked reads, history,
  rollback, feed processing, hosting via `AddPapumaKernelLocal`). Same
  document-sourcing model, no server required. See
  `docs/analyses/local-kernel-sqlite-sibling.md` for the design rationale
  and what's deliberately different per engine (no RLS, no gapless-read
  handling, in-process wakeup instead of LISTEN/NOTIFY — none of that
  applies to a single writer). Every connection now goes through a shared
  `SqliteConnectionFactory` that enables WAL journal mode and a 5s busy
  timeout, so a background feed processor writing doesn't block the UI
  reading chat/document history (verified empirically, not assumed — see
  `SqliteConnectionFactoryTests`).
- **Fix**: declared keys (`UniqueKey`/`LookupKey`) on non-string fields
  (numbers, booleans) now work correctly on `Papuma.Kernel.Local` —
  `LoadByKeyAsync`/`PatchWhereAsync`/`DeleteWhereAsync` previously compared
  every key value as TEXT, but SQLite's `json_extract` returns
  INTEGER/REAL/0-1 for JSON numbers and booleans and never considers those
  equal to TEXT, so non-string keys silently matched nothing. Also fixed:
  the declared-key expression index was never actually used by any of these
  lookups (SQLite only matches an expression index when the query's
  expression is textually identical to the index's, and the JSON path was
  being bound as a parameter, not a literal) — every keyed lookup, including
  ones that "worked" on string keys, was doing a full table scan. Both
  confirmed empirically and fixed; see `SqliteSpikeTests` and
  `SqliteNumericKeyTests`.
- **Internal restructuring**: extracted the storage-neutral diff engine, policy
  engine, model, and validation code (previously `Changes/`, `Model/`,
  `Validation/`, and the storage-neutral half of `Tenancy/`) into a new
  `Papuma.Kernel.Core` project, referenced by `Papuma.Kernel` and embedded in
  its package (`Papuma.Kernel.Core.dll` ships inside the `Papuma.Kernel`
  nupkg, not as a separate published package). Preparation for a future
  SQLite-backed sibling for desktop/embedded use
  (`docs/analyses/local-kernel-sqlite-sibling.md`). No public API or behavior
  change; namespaces are unchanged.
- **Docs**: `README.md` and `docs/factsheet*.md` rewritten to position
  `Papuma.Kernel.Local` as an equal-billing second product rather than a
  footnote — shared/Postgres-only/SQLite-only feature grouping, a
  `Papuma.Kernel.Local` maturity statement, dual quickstart snippets, and a
  new §8 in the technical factsheet. `llms.txt` and the `docs/ai/` set
  (playbook, AGENTS.md snippet, slice conventions) updated for the same
  reason — they were entirely Postgres-framed and would have misled a coding
  agent building against `Papuma.Kernel.Local`. The `Papuma.Kernel.Local`
  package itself now bundles its own docs (README, `docs/ai/`, the design
  rationale, `concepts.md`, `gdpr.md`, the ADRs) the same way `Papuma.Kernel`
  already did — previously it shipped none, contradicting what the docs
  claimed.

## 1.0.2 (2026-06-22)

- **Docs** (shipped inside the package under `docs/`): reframed the read-path
  guidance so a projection is the default derived read and a SQL view is a gated
  exception, not a peer choice (concepts §16, the AI agent snippet and playbook).
  Clarified that "real-time / no lag" alone points to `LoadByKeyAsync`, and added a
  structural-hardening note (`LoadMaskedAsync` / ADR-016). No functional change.

## 1.0.1 (2026-06-14)

- **License headers switched to SPDX** across all source files
  (`SPDX-License-Identifier: MIT` + `SPDX-FileCopyrightText: 2026 Harald Lapp`),
  replacing the prose copyright/license comment. Machine-readable for license
  scanners and SBOM tooling; no functional change.
- **REUSE: added `LICENSES/MIT.txt`** with the full license text alongside the
  SPDX headers.
- **Docs** (shipped inside the package under `docs/`): added a learning-oriented
  [tutorial](docs/tutorial.md) (empty folder → feed-driven read model,
  verified end-to-end against PostgreSQL 18); the slice conventions now document
  registration co-located with each slice; modernized the root README and
  refreshed the factsheet.

## 1.0.0 (2026-06-12) — the 1.0 reboot

**Complete rewrite. No migration path from 0.x — this is a different kernel under the
same name.** The 0.x line (relational tables + outbox + explicitly raised events) is
removed; 1.0 is document-sourced CQRS:

- **Documents are the source of truth** (JSONB), the change feed is derived
  automatically as a reversible field diff per write — no ORM, no triggers, no
  hand-written outbox entries (ADR-002/004).
- **PostgreSQL ≥ 18 required**: the write path is a single atomic statement using
  `RETURNING OLD/NEW` with mandatory optimistic concurrency (ADR-001/003).
- **Privacy policies as a kernel concern**: `[SensitiveData]`, `[TrackHash]`,
  `[TrackReference]`, `[DoNotTrack]` (attributes as defaults, fluent overrides) are
  applied before anything reaches the feed — including delete diffs (ADR-007).
- **Schema evolution from day one**: per-type `schema_version`, lazy upcaster chains,
  loud guards against silent back-migration (ADR-005).
- **Sessions are units of work** with per-write savepoints, shared `correlationId`,
  and append-only `RollbackAsync` (ADR-008).
- **Patch and bulk primitives**: `jsonb_set`-based partial updates without loading,
  set-based `PatchWhere`/`PatchMany`/`DeleteWhere`/`DeleteMany` (ADR-012/014).
- **Processing engine**: gapless snapshot reads (`xid8`), NOTIFY wakeup with polling
  as truth, strict per-handler ordering, retry/backoff/poison, checkpoints with
  `FOR UPDATE SKIP LOCKED` leader coordination, rebuild, lag health check (ADR-009/010).
- **Append-only event log** for facts without state truth (`session.AppendAsync`),
  consumed by the same engine, with opt-in per-type retention (ADR-011/013).
- **Bootstrap**: `services.AddPapumaKernel(...)` hosts schema setup, feed workers and
  retention; multi-tenancy (scope predicates + RLS) carried over from 0.x.

Docs: `docs/` (architecture, 14 ADRs, concepts, getting started, recipes).

## 0.0.1-legacy

Final release of the v1 kernel (PostgreSQL event feed, outbox, projections,
GDPR redaction). Superseded by the 1.0 reboot.
