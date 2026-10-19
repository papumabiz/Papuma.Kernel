# Papuma Spec — Canonical JSON and the v2 Hash

Status: **draft 0.1** · normative language: MUST, MUST NOT, SHOULD, MAY as in RFC 2119 ·
decision record: [ADR-025](https://github.com/papumabiz/Papuma.Kernel/blob/master/docs/adr/adr-025-keyed-hash-policy.md) ·
vectors: [vectors/canonical.json](vectors/canonical.json), [vectors/hash.json](vectors/hash.json) ·
independent reference: [reference/canonical_hash.py](reference/canonical_hash.py)

The `Hash` policy ([changes.md](changes.md#3-policies)) records a keyed hash of a field's new
value. For two implementations to produce the same hash, they must hash the same bytes. This
chapter defines those bytes (the **canonical form**) and the hash built on them.

## 1. Canonical form

`canonical(value)` is a UTF-8 text that depends only on the JSON *value*, never on how it was
parsed or printed. It is defined recursively.

| Value | Canonical text |
|---|---|
| `null`, `true`, `false` | the literal |
| string | `"` + escaped content + `"` (§1.1) |
| number | the normalized decimal (§1.2) |
| array | `[` + the canonical elements, in array order, joined by `,` + `]` |
| object | `{` + `"key":value` pairs sorted by key (§1.3), joined by `,` + `}` |

No whitespace is ever emitted. Input whitespace, the original number text and the original key
order carry no meaning. A top-level value may be any JSON value.

### 1.1 Strings

Escape exactly: `"` as `\"`, `\` as `\\`, and the control characters U+0008, U+0009, U+000A,
U+000C, U+000D as `\b`, `\t`, `\n`, `\f`, `\r`. Every other code point below U+0020 is written
as `\u` plus **four lowercase hex digits** (`\u001f`). Everything else is written literally in
UTF-8: `/` is not escaped, U+007F is literal, non-ASCII and non-BMP characters are literal,
U+2028 and U+2029 are literal. **No Unicode normalization** is applied.

A string containing an unpaired surrogate has no canonical form; the implementation MUST fail
(`invalid-string`). (PostgreSQL `jsonb` cannot hold one, so a stored document never has it.)

### 1.2 Numbers

A number is normalized **by value**, at arbitrary precision, from its text:

1. Take the sign, the digits of the integer and fraction parts, and the exponent.
2. If every digit is zero, the result is `0` (so `-0`, `-0.0` and `0e5` are `0`).
3. Otherwise drop leading zeros, absorb the exponent, and drop trailing zeros of the
   resulting digit string by adjusting the exponent. Never emit an exponent.
4. Emit `-` for a negative number, then the digits with the decimal point placed by the
   exponent: `1.5e-3` is `0.0015`, `123e-2` is `1.23`, `1E2` is `100`, `1e21` is
   `1000000000000000000000`.

So `1`, `1.0` and `1e0` have the same canonical text — consistent with the diff rule that
numbers are equal when they have the same value
([changes.md](changes.md#2-diff-computation), rule 4) — and `0.1` and
`0.10000000000000001` do not.

**Limit.** If the canonical text of a number, sign included, would be longer than **1024
characters**, the implementation MUST fail (`number-too-large`) without materializing it. This
bounds the work an exponent such as `1e1000000000` could cause. The limit applies to canonical
text only; it is not a general limit on JSON numbers in documents. A text that is not a JSON
number fails with `invalid-number`.

### 1.3 Object keys

Keys are sorted by comparing them as sequences of **UTF-16 code units** (as RFC 8785 and .NET
`StringComparer.Ordinal`). Keys are written as strings (§1.1). Beware: comparing UTF-8 bytes (the
natural order in Rust or Go) gives a different order for characters outside the Basic
Multilingual Plane than for U+E000 to U+FFFF; the vector
`object-keys-sort-by-utf16-code-units` pins it.

## 2. The v2 hash

```text
hash = "v2:" + kid + ":" + hex( HMAC-SHA-256( tenantKey, UTF-8( canonical(value) ) ) )
```

`hex` is 64 lowercase digits. `kid` is the identifier of the master key
(`[A-Za-z0-9_-]{1,32}`). The hashed value is the field's new value; a removed field has no
`hash`; an explicit JSON `null` hashes `null`.

### 2.1 Key derivation

```text
tenantKey = HKDF-SHA-256( ikm    = masterKey,
                          salt   = ASCII("papuma-hash-v2"),
                          info   = UTF-8("tenant:" + tenantId + U+0000 + "type:" + typeName),
                          length = 32 )
```

HKDF is RFC 5869 (extract-then-expand). `tenantId` is the empty string for platform scope.
`typeName` is the document type, or the event type for event payloads. The master key MUST be
at least 32 bytes.

### 2.2 The key-check value

```text
checkValue = hex( HMAC-SHA-256( deriveTenantKey(masterKey, "", "papuma:key-check"),
                                ASCII("papuma-hash-check") ) )
```

An application stores it per `kid` and refuses to start when an instance computes a different
value for the same `kid` (ADR-025, point 5).

## 3. Conformance

An implementation conforms when it reproduces every case of
[vectors/canonical.json](vectors/canonical.json) — the canonical text, or the stated error — and
every key, hash and the key-check value of [vectors/hash.json](vectors/hash.json), which fixes a
test master key (`000102…1f`) and `kid` `test`. The vectors were produced by the Python
reference, which uses only the standard library; the .NET kernel reproduces them
independently (`CanonicalAndHashVectorTests`).

To regenerate after a deliberate change, run `python docs/spec/reference/canonical_hash.py`,
review the diff by hand, and bump the vectors' `version`.

## 4. Open points

- The `kid` and key-provider contract, the key-check table and fail-closed start are specified
  by ADR-025; their behaviour is covered by the kernels' own tests, not by vectors.
- The v1 hash (unsalted SHA-256 of the serializer's JSON text) was not canonical; it is
  read-only and never written again, see [changes.md](changes.md#the-hash-policy-is-keyed-v1-is-read-only).
