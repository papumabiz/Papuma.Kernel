# Recipe: Backup, restore and replication

Status: operations recipe (2026-10-06). The dump/restore path is verified against two
independent PostgreSQL 18 clusters by `BackupRestoreTests` (also the incremental chain), the lost-`LISTEN` behaviour
by `ListenConnectionTests`, both in the kernel's test suite. Statements about
replication and PITR describe PostgreSQL itself and are not kernel-tested — they
follow from the kernel's few cluster-bound facts (§4). PostgreSQL kernel only —
`Papuma.Kernel.Local` is a SQLite file: copy it while the application is stopped, or use
SQLite's online backup.

Background: [concepts §19](../concepts.md#19-checkpoints-backup-and-rebuild-what-is-truth-what-is-derivable)
(what is truth, what is derivable), [ADR-022 §7](../adr/adr-022-snapshot-cursor.md)
(the cluster-bound transaction ids).

## 1. What to back up

**The whole database — one consistent snapshot — not selected tables.** The kernel's
truth (`papuma.document`, `papuma.change`, `papuma.event`), its feed cursors
(`papuma.checkpoint`) and your own tables (read models, ADR-019) belong together: a
snapshot gives the projections *and* the matching checkpoints for free. A backup that
takes `papuma.document` alone loses history and the privacy-relevant redaction state.

Roles and the application's login role are cluster-wide and not part of a
database dump; recreate them from your provisioning (`pg_dumpall --globals-only`
captures them if you have none).

## 2. Backup

Logical, one database — fine up to a few hundred GB and for moving between clusters:

```bash
pg_dump -Fc -d meineapp -f meineapp.dump
```

Physical with point-in-time recovery — the production choice (needs WAL archiving
configured on the server, `archive_command`/`archive_library`):

```bash
pg_basebackup -D /backup/base -Ft -z -P
```

**Incremental** is a physical-backup feature; `pg_dump` is always a full dump. Two
mechanisms, usable together:

- **Continuous WAL archiving** — every committed change is shipped as it happens, so
  the base backup plus the archive restores to any moment (PITR) and the RPO is seconds.
- **Incremental base backups** (PostgreSQL 17+, so on every supported kernel server):
  with `summarize_wal = on`, a base backup can store only the blocks changed since the
  previous one; `pg_combinebackup` merges the chain into a normal data directory.

```bash
pg_basebackup -D /backup/full -c fast
# ... the application keeps running ...
pg_basebackup -D /backup/incr1 -c fast -i /backup/full/backup_manifest
pg_combinebackup /backup/full /backup/incr1 -o /backup/restored   # then start a server on it
```

Tools such as pgBackRest, Barman or WAL-G wrap exactly this (full/differential/incremental
chains, retention, verification, object storage); use one in production rather than
scripts. The kernel suits block-level increments: `papuma.change` and `papuma.event`
only grow, and a physical copy keeps the transaction ids, so a restored chain needs
**no feed repair** (verified by `BackupRestoreTests`: the checkpoint rides along, and
exactly the rows that were undelivered at backup time arrive, once). Do not build a
"backup" from the change feed itself — it lacks purged events, checkpoints and your own
tables, and is an integration channel, not a copy.

Whichever you pick: restore it regularly into a scratch environment. A backup that was
never restored is a hope.

## 3. Restore

```bash
createdb meineapp_restored
pg_restore -d meineapp_restored --no-owner meineapp.dump
```

Then start the application (or call `SchemaManager.EnsureSchemaAsync`). What happens
depends on how the copy was made:

| Restore | Transaction ids | You do |
|---|---|---|
| `pg_restore` into **another cluster** (logical) | foreign to the new cluster | nothing — `EnsureSchemaAsync` detects and repairs it once; rows delivered before the backup may come again (at-least-once), none is lost |
| Physical backup (full or incremental chain), PITR, `pg_upgrade` | preserved | nothing |

After the restore, in this order:

1. **Start one instance** (or run `EnsureSchemaAsync`) and let it finish its startup
   before scaling out. `RepairFeedAfterLogicalRestoreAsync` is also callable explicitly
   and returns `true` if it repaired something.
2. **Projections in the same database** came back with their matching checkpoints —
   nothing to do.
3. **External targets** (Elasticsearch, Redis, a mail service, anything outside the
   database): do not hope they match the restored checkpoint. Reset the checkpoint and
   rebuild projections (`ResetProjectionsAsync()`); effect handlers are idempotent by
   the at-least-once contract, but an effect that fired *after* the backup point fires
   again — know which of yours are not safe to repeat before you restore (concepts §19).
4. **Events with retention** purged before the backup are gone; the backup cannot bring
   them back (ADR-013).
5. **Privacy: a restore revives what was erased.** Erasure and history redaction
   ([GDPR guide](../gdpr.md)) act on the live database; an older backup still holds the
   data. Keep a log of erasure requests outside the database (a request id, the subject,
   the date) and re-apply every erasure *newer than the backup* before the application
   serves traffic again. Limit backup retention to what your erasure deadlines allow.

## 4. Replication and high availability

**Is the kernel safe on a PostgreSQL cluster with a standby? Yes — physical streaming
replication is the supported high-availability setup, with the primary as the only
endpoint the application talks to.** The kernel has three properties that decide this:

| Kernel property | Consequence |
|---|---|
| Every save and every feed cycle **writes** (the document, the checkpoint row, `NOTIFY`) | writers and feed processors connect to the **primary** |
| The feed cursor stores **transaction ids** (ADR-022) | the id space must survive: physical replication and promotion keep it, logical replication does not |
| `LISTEN`/`NOTIFY` is only a wakeup; polling is the source of truth (ADR-010) | losing the `LISTEN` connection costs latency, never correctness |

**Physical streaming replication (primary + hot standby, "master/slave")** — use it.
A failover promotes the standby; the transaction ids are the same, so the feed cursor
stays valid and nothing needs repairing. Point the application at the primary through
whatever follows the promotion (a virtual IP, a DNS name, Patroni/pg_auto_failover, a
managed service's endpoint). Notes:

- **Standbys are read-only.** The kernel does not read from a standby: `LISTEN` is not
  possible there, a feed cycle writes, and a read right after a write could see the state
  from before it (replication lag) — a spurious `ConcurrencyException` or a stale form.
  Use a standby for *your own* reporting queries on read-model tables, never for sessions
  or feed processors.
- **Asynchronous replication can lose the last commits on failover.** The feed
  checkpoints live in the same database as the data, so a lost tail loses documents and
  their feed positions together — the copy stays consistent. What it cannot undo is what
  *left* the database for those commits (a sent mail, a message on a bus): treat a
  failover with lost commits like a restore (§3.3). If that is unacceptable, use
  synchronous replication (`synchronous_standby_names`), accepting the commit-latency
  cost.
- **A failover drops every connection.** Sessions fail with an `NpgsqlException`: a
  save that was in flight is unknown — retry it through your normal optimistic-
  concurrency path. The feed processors reconnect by themselves; their `LISTEN`
  connection is re-established on the next idle wait, and until then they poll
  (verified; releases before this recipe spun the idle loop after a lost `LISTEN` connection).
- **Connection poolers** (PgBouncer): transaction pooling is compatible with the
  kernel's sessions (the scope is transaction-local, `set_config(..., true)`, and the
  coordination uses row locks inside a transaction). The `LISTEN` connection is the
  exception — it needs a session-level connection; behind a transaction pooler
  `NOTIFY` simply does not arrive and the processor polls every `PollInterval`
  (default 5 s) instead. Lower `PollInterval` if that latency matters, or give the feed
  processors a direct connection string.

**Logical replication (publication/subscription) — not for HA, not for the kernel's
tables.** The subscriber assigns its own transaction ids, so replicated `txid` values
and cursor snapshots are foreign there — the same state as a logical restore (§3), but
it does not stay repaired, because every replicated row arrives with a fresh foreign
id; and sequences are not replicated. Use logical replication for what it is meant for
(feeding a *read model* elsewhere): consume the kernel's feed instead
([external read models](external-read-models.md), [NATS bridge](nats-bridge.md)).

**Several application instances on one primary** are fine and are not replication:
`FOR UPDATE SKIP LOCKED` makes extra instances failover, not throughput (concepts §14).

## 5. Checklist

- [ ] Production: physical backups with WAL archiving (a tool like pgBackRest or Barman), not just `pg_dump`.
- [ ] Backup covers the whole database; restores into a scratch environment run on a schedule.
- [ ] Backup retention is shorter than your erasure deadlines — or erasures are replayed after a restore.
- [ ] Erasure requests are logged outside the database.
- [ ] For HA: physical streaming replication, one endpoint that follows the primary; no logical replication of `papuma.*`.
- [ ] The application never connects to a standby.
- [ ] Failover drill: promote the standby in staging, watch lag return to 0 and sessions recover.
- [ ] You know which effect handlers are unsafe to repeat.
