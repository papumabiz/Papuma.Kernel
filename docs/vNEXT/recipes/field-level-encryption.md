# Recipe: Field-level encryption for must-decrypt-elsewhere data

Status: pattern recipe (2026-06-13). Background: [ADR-007](../adr/adr-007-privacy-policies.md)
(policies), [concepts §27](../concepts.md) (why encryption sits *below* the
policies), [concepts §23](../concepts.md) (the store holds plain text).

Some fields must be **recoverable at a different trust boundary** than where they
are written: a bank account (IBAN) is captured by the web/app tier but only the
isolated payout service may read it in clear to initiate a transfer. None of the
privacy policies fit this — they are about the *feed*, and they are one-way:

| Need | Mechanism |
|---|---|
| Never need the value again, only "changed" | `Redact` |
| Compare without content (password) | `Hash` (one-way) |
| Value lives elsewhere in the same system | `Reference` |
| **Recover the value at another trust boundary** | **application-level encryption (this recipe)** |

Encryption is the only option that allows *recovery elsewhere* — and that is
exactly why it is application code, not a policy (concepts §27).

## The storage pattern

Encrypt the value **before** it enters the document; the field then holds only
ciphertext (a Base64 string). The kernel treats it as an ordinary string — it
never sees the plaintext.

```csharp
public sealed record BankAccount(
    string Id,
    string Holder,
    [property: SensitiveData] string IbanCiphertext); // Redact: ciphertext stays out of the feed

// Write tier (has the public key / KMS encrypt permission only):
var ciphertext = await _encryptor.EncryptAsync(iban);   // envelope encryption / KMS
await session.SaveAsync(new BankAccount(id, holder, ciphertext), expectedVersion: 0);

// Payout service (the only holder of the private key / KMS decrypt permission):
var account = await session.LoadAsync<BankAccount>(id);
var iban = await _decryptor.DecryptAsync(account!.Document.IbanCiphertext);
```

## Where the value lives (the precise picture)

- **Plaintext:** nowhere persistent — only momentarily in the encrypting process.
  Stronger than any policy, which keeps the store in clear text (§23).
- **Ciphertext in the store** (`papuma.document.data`): always — that is the
  point, so the payout service can load and decrypt it.
- **Ciphertext in the feed:** depends on the field's policy, and this is why the
  attribute matters:
  - `Track` (default) → ciphertext lands in the diff. **Avoid.**
  - `Redact` → the feed shows only `{"changed": true}` (the *fact* of change, no
    ciphertext). **Recommended:** "the IBAN changed" is a legitimate audit/fraud
    signal worth keeping, while the value — even encrypted — should not spread
    into the broadly-consumed, versioned feed.
  - `DoNotTrack` → the field never appears in the feed at all. Use only when even
    the change signal is sensitive.

Why keep ciphertext out of the feed: the feed is append-only, widely consumed
(projections, polyglot consumers, a bus) and **versioned**. Spreading ciphertext
there multiplies copies (larger blast radius if a key ever leaks) and the version
history would retain *old* ciphertext under *old* keys — the opposite of what you
want across key rotation. The store keeps only the current version.

## Doing the crypto right

- **Use envelope encryption or a KMS/HSM**, not raw asymmetric over the field.
  In practice: a KMS (AWS KMS, Azure Key Vault, GCP KMS, Vault) holds the private
  key so it never leaves the HSM; the write tier gets *encrypt* permission, the
  payout service gets *decrypt* permission. For raw asymmetric, encrypt a per-record
  data key (envelope) and store the wrapped key alongside the ciphertext.
- **Never roll your own crypto.** Use a vetted library/KMS; get IVs, padding,
  authenticated encryption and key rotation from it, not from hand code.
- **Key rotation:** because only the current ciphertext lives in the store (not a
  feed history of it), re-encrypting on the next write is enough to roll forward;
  envelope encryption lets you rotate the KMS key without touching records.

## Why the kernel deliberately does not do this

A transparent `[Encrypted]` policy that the kernel decrypts on load would force
the private key onto the *writing* system — where the kernel runs and where the
asymmetric threat model says it must **not** be. Framework-side decryption would
*undermine* the security model, not support it. Plus key management is
deployment-specific (KMS/HSM/Vault, rotation, audit), which the kernel delegates
exactly as it delegates authentication and tenant resolution. Encryption belongs
at the trust boundary where the key lives — and only the application knows that
boundary (concepts §27).
