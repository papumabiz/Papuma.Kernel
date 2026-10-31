# Concept: an offline-capable chat on Papuma Kernel

Status: **concept / not decided** (2026-10-20). Applies
[offline-sync-and-projection-conflicts.md](offline-sync-and-projection-conflicts.md)
(§2 outbox + replay, §6 gaps, §7 collaborative editing) to one concrete product: a chat
where devices keep a SQLite database ([`Papuma.Kernel.Local`](local-kernel-sqlite-sibling.md))
and sync with a central PostgreSQL kernel. Nothing here is implemented; it assumes the
trigger of that document's §2.6 has fired (a real offline requirement). Where this concept
disagrees with or goes beyond that document, it says so.

---

## 1. Summary of the approach

1. **One document per message** — append-only, id generated on the client. Offline sends
   are therefore *creates*, which cannot conflict.
2. **Local-first.** The client always writes to its local SQLite kernel; a background
   replayer pushes to the server (§6 point 6 of the offline document, "local-first").
   There is no second, online-only write path.
3. **The outbox records intent, the change feed records fact.** They are different things
   (§3); the feed is never transferred between systems.
4. **Local and server are two independent kernels** with their own versions, feeds and
   projections. Sync happens at the level of document operations, not feed rows.
5. **Conflicts are confined to edits, deletes and read state** and each has a cheap,
   mostly automatic strategy (§5).

## 2. Document model

| Document | Key / id | Contents | Written by |
|---|---|---|---|
| `Conversation` | client-generated id | title, kind (direct/group) | creator; rarely changes |
| `Membership` | `(conversationId, userId)` | role, joinedAt, leftAt | admin actions; server-authoritative |
| `Message` | client UUIDv7 | conversationId, senderId, text, clientCreatedAt, replyTo, editedAt?, deletedAt? | sender only |
| `ReadState` | `(conversationId, userId)` | `lastReadMessageId` | reader, from any device |

Decisions and why:

- **Message = own document, not an entry in a conversation document.** A shared message
  list would make every concurrent send a version race on the conversation (ADR-003).
  Separate documents make concurrent sends independent by construction.
- **Client-generated ids (UUIDv7).** The id exists before the server has seen the message,
  so UI, local projection and outbox can reference it. UUIDv7 is roughly time-ordered,
  which keeps index locality. Natural-key uniqueness (ADR-006/020) is *not* used for
  messages: offline duplicates of a natural key cannot be prevented (offline document §6.3).
- **Only the sender writes `Message`.** Edit and delete are single-writer operations, so
  the multi-writer conflict surface of a message is empty. That is what makes chat easy.
- **Delete is a tombstone first** (`deletedAt`, `text` redacted/cleared), a hard
  `DeleteAsync` only for erasure (§8).
- **`ReadState` is monotonic**: it only moves forward. See §5.
- Attachments are *not* documents; they live in a blob store and the message holds a
  reference. Upload is a separate offline queue with the same replay idea.

## 3. Where a message lives — and what the outbox is

For one message, written offline on a device and later synced:

| Place | What is stored | Authoritative? | Lifetime |
|---|---|---|---|
| Device `document` | the `Message` | yes, until synced; then the server wins | permanent |
| Device `change` | diff of the create (contains `text`) | derived | permanent (see retention gap, §8) |
| Device projection | row in the local chat-view table | derived, rebuildable | permanent |
| **Device outbox** | the *intent* "create Message X" | transient work item | until acknowledged |
| Server `document` | the `Message` | **the** source of truth | permanent |
| Server `change` | diff of the create | derived | permanent |
| Server projections | chat view, search, unread counters | derived, rebuildable | permanent |

So the worst case is more than the three copies of a single database: per device 3 + a
short-lived outbox entry, on the server 3 or more. The outbox entry is not an event log —
it exists to be deleted after acknowledgement.

**Why the outbox is not the change feed.** The feed is a gapless, commit-ordered
record of what *has been committed locally* (ADR-010); its consumers are projections.
The outbox is a queue of what the server *has not seen yet*. A change-feed row cannot be
replayed as-is against the server: the server assigns its own versions, and replaying a
feed row would also feed server-origin changes back (offline document §6.1). Reusing the
feed as an outbox would additionally couple replay cursors to projection checkpoints.

Outbox entry (an application table in the same SQLite file, not a kernel concern, §2.5):

```
outbox(local_seq PK, op, document_type, document_id, base_server_version,
       payload, created_at, state, attempts, last_error)
-- state: pending | in-flight | acked | failed(conflict) | failed(rejected)
-- idempotency / ordering key on the wire: installationId:local_seq
```

Open implementation question: the outbox row should be written **in the same SQLite
transaction** as the document save, otherwise a crash between the two loses or duplicates
an intent. Whether the Local session lets an application table join its transaction needs
checking in `Papuma.Kernel.Local`; if not, the kernel needs a small hook (see §9).

## 4. Flows

### 4.1 Send (offline or online — same path)

1. UI creates `Message { id = uuidv7, clientCreatedAt, text, … }`.
2. One local transaction: `SaveAsync(message, expectedVersion: 0)` + outbox insert with
   `op = create`. The local feed feeds the local chat-view projection; the message shows
   up immediately.
3. UI state "sending / sent / failed" is derived from the **outbox row**, not stored in
   the document (it is device-local and must not sync).
4. Replayer (when online): reads `pending` entries in `local_seq` order, calls the server
   `SaveAsync(message, expectedVersion: 0)` with `ActorId = deviceUserId`, marks `acked`.

### 4.2 Idempotent replay of a create

Retries are certain (timeouts after the server has committed). The offline document lists
`IdempotencyKey` as an existing primitive — **it is not in `src/` today** (no match
for the identifier). Until it exists, a create is made idempotent by its own shape:
`expectedVersion: 0` + client-generated id means a duplicate replay yields
`ConcurrencyException(expected 0, actual 1)`. The replayer then loads the server
document; if it equals the intended message, treat the entry as acknowledged. This costs
one read per duplicate and needs no kernel change. An `IdempotencyKey` would still be
the cleaner solution for edits/patches (§4.3) — a gap in the offline document to
correct.

### 4.3 Edit and delete (sender-only, ordered)

- Edit → `PatchAsync` on `text`, `editedAt` with the remembered server version.
- Delete → `PatchAsync` setting `deletedAt` and clearing `text`.
- The replayer sends a document's operations in `local_seq` order and stops that
  document's lane on failure; other documents continue. A create always precedes its edit.
- Edits of a message the sender has not yet synced are **coalesced** in the outbox
  (still `create`, with the newest text) — fewer round trips, and the server never sees
  a draft.

### 4.4 Receive (pull) — the part the offline document flags as missing

Server → device needs a **pull path** (offline document §6.1/§6.2), which is a real
addition, not an add-on package:

- Per device, a cursor on the **server feed** (`seq` / snapshot cursor, ADR-022),
  filtered to the conversations the user is a member of (scope filter on pull, §6.4).
- Pulled changes are applied to the local kernel keeping the **server's version and
  seq**, marked with an **origin = server** marker so they never enter the outbox.
  Local projections run on them as on any other change.
- A `sync_state(document_id, local_version, server_version)` table maps the two version
  lines (§6.2). A replay uses `base_server_version`, not the local version.
- Own messages come back through the pull as well; matching on the id makes that a
  no-op update (version now server-assigned).

### 4.5 Online fast path

When connected, the server pushes "feed advanced" via the existing notification channel
([realtime-ui-notifications](../recipes/realtime-ui-notifications.md)); the device then
pulls immediately. This is a latency optimization over the same pull; nothing new
semantically. Presence and typing indicators are not documents (offline document §7:
live collaboration is a separate channel).

## 5. Conflict analysis

| Case | Conflict? | Strategy |
|---|---|---|
| Two devices of different users send | none — different ids | n/a |
| Same user, two devices send | none — different ids | n/a |
| Retry of the same send | duplicate, not conflict | §4.2 |
| Edit on one device while the same message is deleted on another | yes (same sender) | **delete wins** (tombstone is terminal) |
| Same message edited on two devices | yes | field LWW on `text` via `Patch`, ordered by `clientCreatedAt` of the edit; both diffs stay in the feed, so a UI can show history |
| `ReadState` moved from two devices | yes | **monotonic max**: replay as `Patch` that only applies if the new id is newer; never moves backwards |
| Member removed while offline-sent messages queue up | yes (authorization, not data) | server rejects at replay (`failed(rejected)`); UI shows "not delivered"; **compensation**, not merge (§6.3) |
| Message order | n/a | see below |

**Ordering.** There is no global order offline. Display order is
`(server seq if synced, else clientCreatedAt)` with unsynced messages pinned to the
bottom of the sender's own view. After sync, order is the server's feed order, which is
by construction linear (ADR-010). Chat users tolerate a short reordering; strict
causality across devices is not promised. If causal replies must never precede their
target, the replayer sends `replyTo` targets first (already implied by `local_seq`
order for one device).

Conflicts that must be surfaced (a rejected message, a delete-vs-edit) are recorded
as `AppendEventAsync("ConflictResolved", …)` per the offline document §2.5, carrying both
actor ids.

## 6. Projections

- **Device**: a SQLite chat-view table maintained by a local projection handler
  (idempotent per `(handler, seq)`, ADR-009). Rebuildable from the local feed.
  *Pending / failed* markers are joined from the outbox at read time.
- **Server**: chat view per conversation, full-text search, unread counters — dumb
  handlers (ADR-009/024), same-database tables with row-level security
  ([same-database-read-models](../recipes/same-database-read-models.md)).
- Unread count = messages after `ReadState.lastReadMessageId`, derived, never stored on
  the device as truth. A server-side counter is a projection of `Message` and
  `ReadState` changes.
- Because the server's feed only carries decided state, **no projection ever sees a
  conflict** (offline document §4): resolution happens at replay, before the feed row
  exists.

## 7. What exists vs. what must be built

| Need | Today | Work |
|---|---|---|
| Local store, feed, projections on SQLite | `Papuma.Kernel.Local` | none |
| `expectedVersion`, `Patch`, `Increment`, `actor_id`, typed concurrency errors | kernel | none |
| Transactional outbox next to the document save | not available (verify) | small kernel hook **or** documented pattern |
| `IdempotencyKey` | **not in code** | optional; §4.2 works around it for creates |
| Replayer + conflict policy per document type | — | new extension package (offline doc §2.5) |
| Pull path with origin marker, server version/seq kept | — | addition to `Papuma.Kernel.Local` (offline doc §6.1) |
| `sync_state` and per-device server cursor | — | extension package + a read endpoint |
| Scope-filtered, policy-projected pull | ADR-016 reads exist server-side | wire them into the pull endpoint |
| Blob store / attachment queue | — | separate, out of kernel scope |

## 8. Privacy and retention (chat specifics)

- `Message.text` is personal data. Declare a field policy (`Redact`, `Reference` or field
  encryption, [field-level-encryption](../recipes/field-level-encryption.md)); otherwise
  the plain text sits in **every diff on every device and on the server** (see
  [gdpr.md](../gdpr.md): delete cleans the feed only for policy-protected fields).
- Erasure order on the server: `DeleteAsync` (or tombstone) → `RedactHistoryAsync` →
  rebuild or clean projections (own tables are *not* covered by the kernel primitives).
- **Erasure must propagate to devices**: a redaction is an event the pull has to deliver
  and the device has to apply to its local feed and projection. A device that never
  syncs again keeps its copy (offline document §6.4) — an unavoidable limit; mitigate
  with a server-side "revoke device" that the app honours on next start.
- **Retention**: `papuma.change` and the local `change` table are never pruned (see
  the time-partitioning note in [concepts.md](../concepts.md)). Chat volume makes this a real
  cost on the server and on devices; a device-side compaction (drop old diffs of
  messages older than N days, keep the document) is a Local addition worth deciding
  early.
- The outbox holds plain `payload` until acknowledged; it deserves the same at-rest
  protection as the database file and must be purged on acknowledgement and on logout.

## 9. Decisions to make before building

1. **Local-first (recommended) vs. online-first with offline fallback.** Local-first
   gives one write path and instant UI; it moves server-side guarantees (membership,
   keys) to replay time, which for chat is acceptable (compensation = "not delivered").
2. **Outbox transaction**: can an application table join a Local session's SQLite
   transaction? If not, add the hook before anything else — it is the correctness core.
3. **Edit semantics**: allow edits after delivery at all? Time-boxed edits shrink the
   conflict surface to near zero.
4. **Pull filter**: which conversations/history depth a new device receives (full
   history vs. last N days + on-demand).
5. **Collaborative features** (shared drafts, message reactions, rich text): reactions
   are a separate append-only document per `(message, user, emoji)` — no conflict. Shared
   rich-text editing would need the CRDT path of §7 of the offline document; out of scope
   here.
6. **Retention/compaction** for diffs on devices and server (§8).

## 10. Suggested order of work

1. Verify the outbox-in-transaction question in `Papuma.Kernel.Local` (point 2 above).
2. Server-side model + projections for the *online-only* chat (no new code beyond the
   documents above) — this is already supported and is the baseline of the offline
   document §1.
3. Local-first writes plus outbox plus replayer for **create only**; measure.
4. Pull path with origin marker and `sync_state`.
5. Edit/delete lanes, `ReadState`, conflict events.
6. Erasure propagation and device revocation.
