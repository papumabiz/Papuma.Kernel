# ADR-025 — The Hash policy is keyed and canonical: HMAC-SHA-256, a derived key per tenant

## Status

Accepted (2026-10-19) — supersedes the algorithm of the `Hash` policy in
[ADR-007](adr-007-privacy-policies.md) (an unsalted SHA-256 of the value's JSON text);
the policy catalog, its attribute and the diff entry shape stay. Both kernels, the change
feed, the event log and masked reads. Ships in the next major version (see Consequences).

Implemented: canonical JSON and the v2 hash (`CanonicalJson`, `HashV2`), the key provider
(`IHashKeyProvider`, `MasterKeyHashKeyProvider`), the options, the key context in all three policy
appliers, the key-check table with the startup verification (point 5), fail closed for a model with a
`Hash` policy and no key (point 4, stores and hosted startup), the test key
(`Papuma.Kernel.Testing.TestHashKeys`), and v1 as a read-only form (point 6): the kernel no longer
has a code path that writes an unkeyed hash.

## Context

`Hash` (`[TrackHash]`) records `{ "changed": true, "hash": "…" }` instead of a value: the
feed shows *that* a field changed and lets a consumer tell whether two records carry the
same value, without the content. Today the hash is the lowercase SHA-256 of the UTF-8 bytes
of `JsonNode.ToJsonString()` of the new value (`PolicyHash.Of`), used in three places: the
feed diff, the policy-projected (masked) read — which promises "the same hash the feed would
show" (ADR-016) — and event payloads (ADR-013).

Writing the storage-neutral specification ([roadmap](../analyses/interop-and-hardening-roadmap.md),
finding F2) exposed two defects:

1. **It is not canonical.** The hashed text is whatever `System.Text.Json` prints: key order
   of an object value, escaping, and the original text of a number (`1` vs `1.0`, although
   the diff engine treats them as equal). A second implementation, or a serializer update,
   produces different hashes for the same value. The spec's number rule (equal by value) and
   the hash disagree.
2. **It is unsalted.** A plain SHA-256 of a low-entropy value — a phone number, a date of
   birth, a short customer number — is recovered by a dictionary or by exhaustive search in
   seconds. The feed is the part of the system that is read by other processes (search
   indexes, NATS consumers, the polyglot clients of the wire format); ADR-007's promise that
   the feed is free of content does not hold for the fields where `Hash` is most tempting.

A salt stored next to the feed does not help: whoever can read the feed can read the salt.

## Decision

### 1. The algorithm

```text
hash = "v2:" + kid + ":" + hex( HMAC-SHA-256( tenantKey, canonical(value) ) )
```

- `value` is the new value of the field, as today; a removed field still has no `hash`; an
  explicit JSON `null` hashes `canonical(null)`.
- `canonical(value)` is a byte-exact UTF-8 serialization defined in the specification
  (`docs/spec`, canonical-JSON chapter, with test vectors): no insignificant whitespace;
  object keys sorted by UTF-16 code units (as RFC 8785); strings escaped minimally and
  without Unicode normalization; **numbers normalized by value** — no exponent, no leading or
  trailing zeros, `-0` is `0` — so `1`, `1.0` and `1e0` hash alike, consistent with the diff
  rule that they are equal. The ADR fixes the principles; the chapter fixes the bytes.
- `kid` identifies the master key (`[A-Za-z0-9_-]{1,32}`); `hex` is 64 lowercase digits.
  The whole string is opaque to consumers.

### 2. The key hierarchy

One **master key** (at least 32 random bytes) is configured per application, together with
its `kid`. The key used for a value is derived per tenant and type:

```text
tenantKey = HKDF-SHA-256( ikm  = masterKey,
                          salt = "papuma-hash-v2",
                          info = "tenant:" + tenantId + "\0type:" + typeName,
                          length = 32 )
```

`tenantId` is empty for platform scope; `typeName` is the document type, or the event type
for event payloads. The same value therefore hashes differently in another tenant and in
another type: hashes cannot be correlated across tenants, and an equality comparison only
ever makes sense — and works — inside one tenant and type.

### 3. `IHashKeyProvider`

Key access sits behind one small interface in `Papuma.Kernel.Core`:

```csharp
public interface IHashKeyProvider
{
    string ActiveKeyId { get; }
    ReadOnlyMemory<byte> GetKey(ScopeContext scope, string typeName);   // pure, stable, 32 bytes
}
```

The default implementation derives as above from a master key supplied through options
(`PapumaKernelOptions.HashMasterKey`, base64, and `HashKeyId`) — typically read from a
secret store or environment variable; the kernel never logs it. An application that wants
real per-tenant keys (and with them per-tenant destruction of a key) implements the
interface against a KMS; nothing else in the kernel changes. `GetKey` must be pure: the same
inputs return the same bytes for the life of a `kid`.

### 4. Fail closed

If the model declares any `Hash` policy and no `IHashKeyProvider` is configured, store
construction (and the host's startup validation) fails with `InvalidOperationException`
naming the types concerned. There is no fallback to the old algorithm and no unkeyed mode.
`Papuma.Kernel.Testing` supplies a fixed test key (`kid` `test`), so tests need no
configuration.

### 5. The key-check value

A wrong key on one instance would silently produce hashes that match nothing. The kernel
therefore keeps a check value per `kid` in a new table `papuma.hash_key_check`
(`kid`, `check_value`, `created_at`), where

```text
check_value = hex( HMAC-SHA-256( GetKey(platform scope, "papuma:key-check"), "papuma-hash-check" ) )
```

At startup each instance computes it for its `ActiveKeyId`, inserts it if the `kid` is new
(`ON CONFLICT DO NOTHING`), reads the stored value, and **refuses to start** if the two
differ (`HashKeyMismatchException`: names the `kid`, never prints a key or a check value).
This catches a misconfigured instance, a swapped secret and a half-finished rotation. It
cannot catch a per-tenant provider whose key for one tenant is wrong — that is the
provider's contract.

### 6. v1 is read-only

After this ADR the kernel never writes a `hash` without the `v2:` prefix. Existing records
keep their 64-hex SHA-256 values: the feed is history and is not rewritten, and nothing in
the kernel parses a hash, so reading, replaying and exporting them is unchanged. The wire
format documents both forms; a consumer treats the value as an opaque string and compares
only values of the same form. Masked reads produce `v2` hashes (they need the scope and the
type, which the projector now receives), so "the same hash the feed would show" holds for
every record written after the upgrade.

### 7. Rotation and loss

Rotation = configure a new `kid` (and key). New hashes carry the new `kid`; old ones stay as
they are. Hashes across a `kid` boundary never match, exactly like the boundary between `v1`
and `v2`. The kernel needs old keys for nothing — it never verifies a hash — so an
application keeps them only if it wants to check a candidate value against historical
hashes. Losing the master key therefore loses no data (documents are the truth; a hash is
derived feed content) and breaks no reading; it ends comparability with the history, and a
new key is simply a new `kid`. A **leaked** key is the serious case: it makes the hashes
written under it attackable by dictionary, so it belongs in a secret store and rotation
after a leak is the response.

## Consequences

- **Positive:** A dictionary attack on a `Hash` field needs the key, which feed consumers
  (search, bus, polyglot clients) never hold. The feed's "no content" promise holds for
  low-entropy values again, as far as a keyed hash can deliver it.
- **Positive:** Hashes are reproducible across implementations: the canonical form, the
  derivation and the output are fixed by the specification and its vectors, and consistent
  with the number-equality rule.
- **Positive:** Per-tenant derivation stops cross-tenant correlation without storing a key
  per tenant; the provider interface leaves room for per-tenant keys from a KMS.
- **Positive:** A misconfigured instance stops at startup instead of writing hashes that
  match nothing, and the failure is a typed exception with the `kid`.
- **Negative:** **A key to manage.** Every writing process of an application — web
  instances, workers, and later a Rust client on the same database — needs the same master
  key. Operating a secret is a new duty for every user of `[TrackHash]`.
- **Negative:** **Breaking on upgrade.** An application using `[TrackHash]` fails at startup
  until a key is configured, so this ships in a major version (3.0); the changelog and the
  upgrade note say so. A warn-and-continue mode was rejected (below).
- **Negative:** **Comparability ends at the boundary.** A `v1` hash and a `v2` hash of the
  same value never match, nor do two `kid`s. The old history keeps its weak `v1` hashes;
  re-hashing is impossible because the value is gone.
- **Negative:** **Equality stays visible, by design.** Records sharing a value share a hash
  — for a boolean or a small enumeration that is the value itself. A keyed hash is not
  encryption; where the value must be read back, use field-level encryption
  ([recipe](../recipes/field-level-encryption.md)).
- **Negative:** **The key is the security boundary, and derivation ties tenants together.**
  Anyone holding the master key can attack every tenant's hashes; destroying it ends
  comparability for all tenants at once. Per-tenant destruction needs a per-tenant provider.
- **Negative:** A keyed hash is still pseudonymous personal data under GDPR. Destroying all
  keys makes the hashes unattributable in practice, but that is a legal assessment for the
  application ([gdpr.md](../gdpr.md)), not something the kernel promises.
- **Neutral:** Cost is one HKDF per (tenant, type) — cacheable — and one HMAC per hashed
  field per write; negligible next to the write.
- **Neutral:** Removing the weak `v1` hashes from history is a separate, explicit act, proposed in
  [ADR-027](adr-027-strip-legacy-hashes.md): it reduces them to `{ "changed": true }` without needing
  the values.

## Alternatives considered

- **Keep the unsalted SHA-256, only canonicalize it.** Fixes defect 1, leaves the one that
  matters for personal data. Rejected.
- **A salt (or pepper) stored in the database.** Defeats precomputed tables but not a reader
  of the feed, who can read the database. A pepper held outside is a key; HMAC is the
  standard construction for it and avoids inventing one.
- **A slow password hash (Argon2, bcrypt) per value.** Makes each guess expensive, but a
  five-digit or date-shaped value falls to it anyway, it costs milliseconds on every write
  of a hashed field, and its per-hash random salt destroys the equality property `Hash`
  exists for. Rejected.
- **A random salt per record.** The hashes can no longer be compared, which is `Redact`.
- **One global key, no derivation.** Simpler, but the same value is then linkable across
  tenants and types. Derivation costs one HKDF call.
- **A distinct stored key per tenant, always.** Allows per-tenant destruction, but turns the
  kernel into a key manager (storage, rotation, backup, access control) — the same reasoning
  as ADR-007's closure on a sensitive store. Offered through the provider interface instead.
- **Warn and fall back to `v1` when no key is configured.** Keeps upgrades painless and keeps
  the weak hashes flowing silently — the failure mode this ADR exists to end. Rejected.
- **Truncate the hash.** Saves bytes in the feed, costs collision resistance for the
  equality semantics. Rejected.

## Related

- [ADR-007](adr-007-privacy-policies.md) — the policy catalog; this ADR replaces the
  algorithm behind `Hash` only.
- [ADR-004](adr-004-changerecord-diff-only.md) — the diff entry shape that carries the hash.
- [ADR-013](adr-013-business-event-log.md) — event payload policies, where `Hash` also acts.
- [ADR-015](adr-015-gdpr-tooling.md) and [gdpr.md](../gdpr.md) — erasure and history redaction.
- [ADR-016](adr-016-policy-projected-reads.md) — masked reads, which show the same hash.
- Recipe [field-level-encryption.md](../recipes/field-level-encryption.md) — when the value
  must be recoverable.
- [Roadmap, phase 2](../analyses/interop-and-hardening-roadmap.md) (finding F2) and the
  specification, `docs/spec/changes.md` §3 (policies) and the canonical-JSON chapter to come,
  in the repository.
