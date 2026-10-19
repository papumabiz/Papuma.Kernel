# Analysis: two wire-format gaps worth closing before anything is live

Status: **analysis / for decision** (2026-10-19). Nothing here is implemented. Follows the finding
in the [interop and hardening roadmap](interop-and-hardening-roadmap.md) (F5 and F7) and the
observation that the kernel is not yet used in production anywhere, only by applications still in
development — which makes a change to the stored and wire form cheap *now* and expensive later,
especially with a second implementation (Rust) about to depend on it.

Measured against the code at `62ea1cf`, by reading and by running probes; effort figures are rough
sizes for one person who knows the code, not estimates to plan a release on.

---

## 1. Paths in the diff are dot-separated strings (F5)

### What exists

A diff, a policy, a key and a GDPR selector all name a field by a string such as `address.city`.
Underneath, the kernel already thinks in **segments**: SQL parameters, key indexes and the patch
vectors all use `string[]`. The dotted string is a serialization of a segment list, and it is
built or taken apart in a small number of places:

| Where | What it does with the string |
|---|---|
| `JsonDiffEngine` | builds `prefix.key` while diffing; `Split('.')` when applying; **rejects** an object key that contains `.` |
| `PolicyProjector`, `DataInventory`, `KernelModelBuilder` (scan) | build `prefix.name` while walking a document or a type |
| `DocumentTypeMetadata.ResolvePolicy`, `DataInventory` | walk to the parent path with `LastIndexOf('.')` |
| `RedactionEngine`, `EventPayloadPolicyApplier`, GDPR selectors (both kernels) | `Split('.')` on caller- or model-supplied paths |
| `JsonPathResolver`, `KeyMetadata`, `PatchBuilder` | join/split property chains from `x => x.Address.City` |

The public surface that exposes the string: `DocumentDiff.Entries` keys, `ChangeRecord.FieldChanged(path)`,
`DocumentTypeMetadata.Policies`, `KeyMetadata.Path`, the `paths` parameter of `RedactHistoryAsync`,
`EventSelector.PayloadPath`, the inventory report, and the field path in two exceptions. Outside the
kernel, the wire format is read by the Python and Go consumers, which treat the keys as opaque
strings (`list(diff.keys())`).

### What actually goes wrong today

Only **dictionary-shaped data with free keys** is affected — `Dictionary<string, T>` with keys such as
`feature.enabled`, an i18n table (`home.title`), settings keyed by host name. A *declared property*
whose JSON name contains a dot is already rejected when the model is built. For a free key the
write fails: `JsonDiffEngine.Diff` throws `NotSupportedException` and **the whole document cannot be
saved**. It fails loudly and never corrupts a diff, but it is a hard limit on what a document may
contain, and a surprising one: the rule lives in a diff engine, not in the model.

### Options

| | Idea | Existing data | Public API / wire | Rough size |
|---|---|---|---|---|
| **A** | Segments as the wire form: JSON Pointer (`/address/city`, `~0` `~1`) | none compatible (dev databases would be reset) | every path string changes: `FieldChanged("/status")`, `Policies`, `KeyMetadata.Path`, inventory, GDPR `paths`, exceptions, vectors, both consumers, ADR-004/007 | 2–3 days plus a review of every doc and sample |
| **B** | Keep the dotted string, **escape** the separator inside free keys: `\.` for a dot, `\\` for a backslash | **identical** for every path without a dot or backslash — i.e. all declared properties | none for declared paths; free keys with `.` or `\` gain an escape; consumers that treat keys as opaque are unaffected | ~½ day: one `DiffPath` helper (escape, join, split, parent), the six sites above, the spec rule and its vectors, the schema pattern |
| **C** | A list of `{path: [...], old, new}` entries instead of an object keyed by path | none compatible | the diff stops being "keys = changed paths"; `Entries` and every consumer change shape | days, and it loses the cheap key-set view |
| **D** | Keep today's behaviour, document the limit | unchanged | none | trivial; the limit stays |

B is the only option that keeps the stored form of everything that works today. It also matches how
the kernel already reasons: the dotted string stays the *serialization*, the segment list stays
the model, and the patch vectors (which already use arrays) are consistent with it. The rule would be:
a separator inside a key is written `\.`, a backslash `\\`; a declared property whose JSON name contains
either keeps being rejected at model build (it is static and renaming is trivial), only free keys are
encoded.

Two details to settle if B is chosen: `ResolvePolicy` must find the parent by the last *unescaped*
separator, and `PathCovers` ("a policy on `settings` covers `settings.feature\.enabled`") keeps working
unchanged because the separator after `settings` is unescaped. A dictionary key containing a backslash
(a Windows path used as a key) is re-encoded — harmless with no live data, a visible change otherwise.

### Recommendation

**Do B if any application has or plans a dictionary-shaped field with free keys; otherwise D and write
the limit down.** A is the standards-based answer but pays for it in breaking every string the
kernel exposes; C is a redesign with no matching benefit. If the answer is "we have no such fields and
do not expect any", D costs nothing now and B stays available later at the same price, because B does
not change anything that exists.

---

## 2. `document_type` is the CLR type name (F7)

### What exists

A document type's name is `typeof(T).Name` (two sites in `KernelModelBuilder`), checked against
`[A-Za-z][A-Za-z0-9_]…` and then used as the logical name everywhere:

- the `document_type` / `event_type` column of every table, and so every stored row;
- `Reference` policy text (`User/123/email`) written into the feed;
- the names of the key indexes (derived from type and path);
- the type segment of the **hash key derivation** (ADR-025): `HKDF(info = "tenant:…\0type:" + name)`;
- metrics tags, MCP tool arguments, GDPR export and inventory, the NATS subject in the bridge recipe.

There is no way to give a type another name, and no attribute or fluent override exists.

### Three problems, one verified

1. **Verified: two types can collide silently.** `Document<SalesArea.Item>()` and
   `Document<StockArea.Item>()` pass `Build()` and both map to `document_type 'Item'`
   (probe run on this checkout). They would share rows, ids and the key space. This is a latent
   data-integrity defect today, independent of any decision here.
2. **A rename is a data migration.** Renaming the C# class changes `document_type`; the stored rows
   are orphaned and loads return nothing. With ADR-025 it also changes the hash key for the type, so
   new hashes of unchanged values no longer match the old ones.
3. **A second implementation inherits a C# detail.** Rust has its own naming habits. The shared
   contract wants the name *declared* in the metamodel, not derived from a language's type name
   (spec, `changes.md` §1 already says so; the kernel does not yet honour it).

### Options

| | Idea | Compatible | Rough size |
|---|---|---|---|
| **A** | Optional explicit name — `[DocumentType("user")]` / `[EventType("user-logged-in")]` and a fluent `.Named("…")`; **default stays `clrType.Name`**; plus a build-time **uniqueness check** across document types and across event types | fully (existing names unchanged) | 2–3 hours: one attribute pair, two sites in the builder, the check, docs, ~5 tests |
| **B** | Explicit name **mandatory** | breaking: every type needs an attribute | A, plus the boilerplate, plus every model and test touched |
| **C** | Namespaced default (`Company.Sales.Item`) | breaking: all stored names change; longer identifiers; the name pattern would need to allow dots | days, and it re-introduces dots into a field this ADR line is trying to keep simple |

The name pattern (`[A-Za-z][A-Za-z0-9_]…`) should **stay as it is**. The SQLite kernel writes the type
into SQL as a validated literal (to match the partial key index), so relaxing the pattern is a security
decision, not a naming one. Logical names are therefore `UserAccount` or `user_account`, not `user-account`.

### Recommendation

**Do A, and split off its uniqueness check as the first, independent fix** — it closes a verified
defect, changes nothing for anyone with distinct names, and can ship on its own. Leave "mandatory" (B)
for later: it buys little once names *can* be declared and a collision *cannot* happen, and it taxes
every application for a problem only some will have. The docs should recommend declaring names for any
type that is expected to outlive a refactoring or to be shared with a second implementation.

---

## 3. How the two fit together

- Both are **compatible with everything that exists** in their recommended form (B for paths, A for
  names), so neither forces a data reset, and neither needs to wait for the 3.0 release — they can
  land in any order, ahead of it or inside it.
- If the answer to paths is A or C instead, that is the one change that *would* break stored data;
  then it belongs in the same major version as the other breaks (hash key, patch errors) and the
  development databases are reset once.
- The spec work follows either way: `changes.md` rule 5 (dotted keys) is rewritten for B, the
  `document_type` text in the wire-format document and the schema gains the "declared name"
  wording for A, and each gets vectors.

## 4. Decisions needed

1. **Paths:** B (escape free keys), D (document the limit), or A/C (a real format change)? Is there a
   dictionary-shaped field with free keys in any of the applications under development?
2. **Names:** A as proposed — optional explicit names plus the uniqueness check — and should the
   uniqueness check go out first, on its own?
3. If either is a go, whether to ship it before 3.0 or inside it.
