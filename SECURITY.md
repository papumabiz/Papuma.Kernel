# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| 3.0.x | ✅ |
| < 3.0 | ❌ — upgrade to 3.0 ([Upgrading from 2.x](CHANGELOG.md#upgrading-from-2x)); 2.x leaks policy-protected values of structs and `[JsonDerivedType]` subtypes into the feed and lets an MCP agent pick any tenant, 2.0.x lets the `All` scope delete across tenants, 1.x feeds can skip records |

Papuma Kernel has not yet carried production traffic (see
[Maturity](README.md#maturity-stated-plainly)). Fixes land on the current minor
version; there are no backports.

## Reporting a vulnerability

**Please do not open a public issue.**

Use GitHub's private vulnerability reporting on this repository
(*Security* → *Report a vulnerability*), which opens a private advisory visible
only to the maintainer.

What helps:

- the affected package and version,
- what an attacker gains (read across tenants, bypass a privacy policy, …),
- a minimal reproduction — ideally a failing test against the real engine.

You can expect an acknowledgement within a few days, an assessment with a
severity and a planned fix window after triage, and credit in the advisory and
the changelog unless you prefer otherwise. Please give a fix a reasonable chance
to ship before disclosing publicly.

## Security-relevant surface

These are the parts where a bug is a vulnerability rather than a defect:

- **Scope and tenant isolation** — `ScopeContext`, the explicit `scope`/`tenant_id`
  predicates, and the PostgreSQL row-level security policies behind them
  ([ADR-001](docs/adr/adr-001-postgresql-18-only.md), [architecture](docs/architecture.md)).
  Which tenant a request gets is decided by *your* `IScopeResolver`: derive it from
  authenticated claims only, never from a client-set header (that is tenant spoofing), and
  register `UseScopeResolution()` after `UseAuthentication()`. The kernel cannot check this
  for you.
- **Privacy policies** — the five-tier catalog applied at write time, before
  anything reaches the immutable change feed ([ADR-007](docs/adr/adr-007-privacy-policies.md)).
- **GDPR tooling** — history redaction, masked reads, export assembly
  ([gdpr.md](docs/gdpr.md), [ADR-015](docs/adr/adr-015-gdpr-tooling.md)).
- **Schema and identifier handling** — the DDL identifier validator on the
  startup schema path.
- **The SQLite connection string of `Papuma.Kernel.Local`** — `DbPath` must stay data
  (a path that smuggles in connection-string keywords is a bug).
- **The ASP.NET Core dashboard** — it is unauthenticated unless you put
  protection in front of it (`RequireAuthorization()`, `RequireHost(...)`, an authorization
  `FallbackPolicy`, or an internal-only host). If the endpoints carry none of the first three,
  the kernel logs **one error** (category `Papuma.Kernel.Dashboard`). That check runs when
  the endpoint table is built, in practice on the first request — **not at host start** — and
  it cannot see a reverse proxy or network rule, so treat it as a nudge, not a guard.
  Exposing the dashboard publicly is a deployment mistake, not a kernel vulnerability. The page itself escapes all dynamic values and sends a
  Content-Security-Policy plus `nosniff` / `no-store` headers; a rendering
  bypass of that escaping is in scope.

## Database roles

Row-level security is the second layer of tenant isolation (layer 1 is the explicit
`scope`/`tenant_id` predicate in every statement). `FORCE ROW LEVEL SECURITY` subjects the table
owner too, but three kinds of role are not bound by it:

- **superusers** and roles with **`BYPASSRLS`** bypass every policy;
- the **table owner** is subject to the policies, yet can `ALTER TABLE … DISABLE ROW LEVEL
  SECURITY`, drop a policy, or `TRUNCATE` (`TRUNCATE` is not subject to RLS at all).

So the application's runtime role must be **none of these**. With `EnsureSchema = true` the
connecting role creates the schema and therefore owns it, which makes the runtime role the owner
in the default setup: for production, run the schema step (`SchemaManager.EnsureSchemaAsync`, or
your own migration) under a separate migration/owner role and run the application as a plain
login role with DML grants on `papuma.*` (the way `Papuma.Kernel.Testing` sets up its
application role). Also set `idle_in_transaction_session_timeout` on that role
([architecture](docs/architecture.md#session--unit-of-work)). Isolation tests must not run as a
superuser, which passes them whether or not isolation works.

## SQLite file security (`Papuma.Kernel.Local`)

`Papuma.Kernel.Local` has no server, no roles and no row-level security: the database **file is
the security boundary**, and the operating system is what protects it. The kernel does not
encrypt, does not set file permissions and does not manage the files it leaves behind.

- **Data at rest is clear text.** The `document` table holds every document in full (the stored
  truth, [gdpr.md](docs/gdpr.md)); `change` holds the diffs, `event` the payloads, `failure` handler
  error texts. Privacy policies keep sensitive values out of the *feed and events*, not out of the
  documents. Use full-disk or per-user encryption of the OS (BitLocker, FileVault, LUKS). An
  encrypted SQLite build (SQLCipher through your own `SQLitePCLRaw` bundle) is a possible
  alternative, but nothing the kernel's tests cover.
- **File permissions are the process's defaults.** SQLite creates the file with the process
  umask (commonly `0644` on Unix) or the ACL the directory gives it (Windows). Put the file in a
  per-user directory (`Environment.SpecialFolder.LocalApplicationData`, never a shared or
  world-readable one), create that directory yourself with restrictive permissions (`0700`, a
  user-only ACL) and, on Unix, start the process with `umask 077`.
- **The `-wal` and `-shm` files hold data too.** The kernel opens every connection in WAL mode:
  next to `app.db` there is `app.db-wal` with recently committed pages (not yet in the main file)
  and `app.db-shm`. They sit beside the database, get the same permissions and exist until the
  last connection closes. Encrypt, protect and delete the three files together. A redaction
  (`RedactHistoryAsync`) rewrites the rows, but the earlier page contents can survive in the WAL and
  in free pages of the main file until they are overwritten or checkpointed; the kernel sets
  neither `PRAGMA secure_delete` nor runs `VACUUM`. If an erasure has to leave no trace in the file,
  do both yourself after the redaction (with all other connections closed).
- **Back up and copy carefully.** Copying `app.db` alone while a connection is open can miss
  committed data that still sits in the WAL, and pairing it with a `-wal` file from another moment
  is not safe either. Copy while the application is stopped (all connections closed), or use
  SQLite's online backup (`SqliteConnection.BackupDatabase`) or `PRAGMA wal_checkpoint(TRUNCATE)`
  first ([backup recipe](docs/recipes/backup-restore.md)). A copy is as sensitive as the original:
  it needs the same encryption and permissions. Do not keep the live file in a folder that a sync
  client (OneDrive, Dropbox, iCloud) uploads; the client copies the three files at different
  moments and puts the clear text in the cloud.
- **No network drives.** WAL needs shared memory on the same host and reliable file locking;
  SQLite documents that WAL does not work over a network file system, and SMB/NFS locking is
  not reliable enough for the single-writer assumption. Keep the file on a local disk, open it from
  one process only.
- **`DbPath` is data, not syntax.** The path is passed as a data source, so `;` or
  `=` in it cannot add connection-string keywords (such as `Mode=ReadOnly`); a path that is empty
  or `:memory:` is rejected at startup. If your application takes the path from user input,
  you still decide which directories it may point to.

## Known limitation: unbounded materialisation

Several operations load everything they touch into memory in one piece; the kernel puts no limit
on the size of the result. This is a deliberate trade-off at the application layer, not a defect
the kernel hides, but it matters when the caller is not trusted or the machine is small:

- **Affected operations (both kernels):** `GetHistoryAsync` (a document's whole history),
  `GetChangesByCorrelationAsync`, `LoadManyAsync`, `PatchManyAsync` / `PatchWhereAsync` and
  `DeleteManyAsync` / `DeleteWhereAsync` (every matching document and its diff), `RedactHistoryAsync`
  and `RedactEventsAsync` (every matching record), and on PostgreSQL `GdprExport.ExportAsync`.
  `LoadAllByKeyAsync` is the paged read (capped by `KeyMetadata.MaxLoadAllLimit`); feed batches are
  bounded by `BatchSize`.
- **Desktop and `Papuma.Kernel.Local`.** There is no memory limit of a server to fall back on: a
  document with a long history, or a `Where` over a large table, can use most of the machine's RAM
  and bring the whole application to a halt. Bound the ids you pass to the `*Many` operations, prefer
  paged reads, and run bulk and redaction jobs in the background.
- **MCP (`Papuma.Kernel.Mcp`, PostgreSQL only).** The tools were the exposed case (an agent can ask for
  "the whole history"): they are capped by `PapumaMcpOptions.MaxRecordsPerCall` (default 500; the
  history and correlation reads fetch at most the cap plus one row, the failure table is capped after
  loading) and report `"truncated"` ([observability](docs/observability.md)). The limit applies per
  call, not per client, so rate-limit the endpoint if agents are not trusted. The document tools read
  only in the scope `PapumaMcpOptions.ScopeResolver` binds the caller to and refuse every call without
  one; an agent cannot pick another tenant by argument (ADR-016 §6).
- **Web applications.** Do not hand a client-controlled id list or key value to a `*Many`/`*Where`
  operation without bounding it in your endpoint.

## Out of scope

- Findings that require an already-compromised database superuser.
- Denial of service by unbounded application input that the application itself
  is expected to bound (see the known limitation above for what the kernel does
  not bound).
- Access to the SQLite file by anyone the operating system lets read it
  ([SQLite file security](#sqlite-file-security-papumakernellocal)).
- Missing hardening in `samples/` — samples are illustrative, not deployable.
