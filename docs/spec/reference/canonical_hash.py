# SPDX-License-Identifier: MIT
# SPDX-FileCopyrightText: 2026 Harald Lapp
"""Reference implementation of the Papuma canonical JSON form and the v2 hash (ADR-025).

Deliberately independent of the .NET kernel: it uses only the Python standard library, so
that vectors generated here are an independent check of any implementation. Run it to
regenerate docs/spec/vectors/canonical.json and hash.json:

    python docs/spec/reference/canonical_hash.py

The vectors are then reviewed by hand (docs/spec/README.md, "Changing the vectors").
"""

import hashlib
import hmac
import json
import re
import sys
from pathlib import Path

# A number whose canonical decimal expansion is longer than this is rejected (changes.md,
# canonical JSON). It bounds the work an exponent such as 1e1000000000 can cause.
MAX_NUMBER_LENGTH = 1024

NUMBER = re.compile(r"^(-?)(0|[1-9][0-9]*)(?:\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?$")


class CanonicalError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def canonical_number(text):
    """Normalizes a JSON number to its value: no exponent, no redundant zeros, -0 is 0."""
    m = NUMBER.match(text)
    if not m:
        raise CanonicalError("invalid-number")
    sign, integer, fraction, exponent = m.group(1), m.group(2), m.group(3) or "", int(m.group(4) or 0)
    digits = (integer + fraction).lstrip("0")
    if not digits:
        return "0"
    exponent -= len(fraction)
    stripped = digits.rstrip("0")
    exponent += len(digits) - len(stripped)
    digits = stripped
    # Exact length of the canonical text, computed before it is built.
    if exponent >= 0:
        length = len(digits) + exponent
    elif len(digits) > -exponent:
        length = len(digits) + 1
    else:
        length = 2 - exponent
    if length + len(sign) > MAX_NUMBER_LENGTH:
        raise CanonicalError("number-too-large")
    if exponent >= 0:
        body = digits + "0" * exponent
    elif len(digits) > -exponent:
        point = len(digits) + exponent
        body = digits[:point] + "." + digits[point:]
    else:
        body = "0." + "0" * (-exponent - len(digits)) + digits
    return sign + body


def canonical_string(value):
    out = ['"']
    for ch in value:
        code = ord(ch)
        if 0xD800 <= code <= 0xDFFF:
            raise CanonicalError("invalid-string")
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif ch == "\b":
            out.append("\\b")
        elif ch == "\t":
            out.append("\\t")
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\f":
            out.append("\\f")
        elif ch == "\r":
            out.append("\\r")
        elif code < 0x20:
            out.append("\\u%04x" % code)
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def utf16_key(key):
    """RFC 8785 order: compare strings as sequences of UTF-16 code units."""
    return key.encode("utf-16-be")


class Number(str):
    """A JSON number kept as its original text (so 1.0 and 1e0 survive until canonicalization)."""


def parse(text):
    return json.loads(text, parse_float=Number, parse_int=Number, parse_constant=_reject)


def _reject(token):
    raise CanonicalError("invalid-number")


def canonical(value):
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, Number):
        return canonical_number(str(value))
    if isinstance(value, str):
        return canonical_string(value)
    if isinstance(value, list):
        return "[" + ",".join(canonical(v) for v in value) + "]"
    if isinstance(value, dict):
        items = sorted(value.items(), key=lambda kv: utf16_key(kv[0]))
        return "{" + ",".join(canonical_string(k) + ":" + canonical(v) for k, v in items) + "}"
    raise CanonicalError("invalid-value")


def hkdf_sha256(ikm, salt, info, length):
    """RFC 5869."""
    prk = hmac.new(salt, ikm, hashlib.sha256).digest()
    okm, block = b"", b""
    counter = 1
    while len(okm) < length:
        block = hmac.new(prk, block + info + bytes([counter]), hashlib.sha256).digest()
        okm += block
        counter += 1
    return okm[:length]


HKDF_SALT = b"papuma-hash-v2"


def tenant_key(master_key, tenant_id, type_name):
    info = ("tenant:" + tenant_id + "\0type:" + type_name).encode("utf-8")
    return hkdf_sha256(master_key, HKDF_SALT, info, 32)


def hash_value(master_key, kid, tenant_id, type_name, value_json):
    key = tenant_key(master_key, tenant_id, type_name)
    mac = hmac.new(key, canonical(parse(value_json)).encode("utf-8"), hashlib.sha256).hexdigest()
    return "v2:" + kid + ":" + mac


def key_check(master_key):
    key = tenant_key(master_key, "", "papuma:key-check")
    return hmac.new(key, b"papuma-hash-check", hashlib.sha256).hexdigest()


# ---- vector generation -------------------------------------------------------------------

MASTER_KEY = bytes(range(32))
KID = "test"

CANONICAL_CASES = [
    ("null", "null"), ("true", "true"), ("false", "false"),
    ("empty-string", '""'),
    ("string-quote-and-backslash", r'"a\"b\\c"'),
    ("string-short-escapes", r'"\b\t\n\f\r"'),
    ("string-other-control-characters-use-lowercase-hex", r'"\u0001\u001f\u007f"'),
    ("string-slash-is-not-escaped", r'"a\/b"'),
    ("string-non-ascii-is-literal", '"Köln"'),
    ("string-non-bmp-is-literal", r'"😀"'),
    ("string-line-separator-is-literal", r'" "'),
    ("number-zero", "0"), ("number-negative-zero", "-0"), ("number-zero-with-fraction", "-0.0"),
    ("number-zero-with-exponent", "0e5"),
    ("number-integer", "42"), ("number-integer-with-fraction-zero", "1.0"), ("number-exponent-zero", "1e0"),
    ("number-exponent-grows-integer", "1E2"), ("number-trailing-zero", "0.10"),
    ("number-negative-decimal", "-1.5"), ("number-exponent-shifts-into-fraction", "1.5e-3"),
    ("number-exponent-shifts-out-of-fraction", "1.23e2"), ("number-fraction-shifted-by-exponent", "123e-2"),
    ("number-small", "0.000001"), ("number-large-exponent-is-expanded", "1e21"),
    ("number-at-the-length-limit", "1e1023"), ("number-fraction-at-the-length-limit", "1e-1022"),
    ("number-beyond-int64", "12345678901234567890"),
    ("number-beyond-double-precision", "0.10000000000000001"),
    ("array-empty", "[]"), ("object-empty", "{}"),
    ("array-keeps-order", '[3,1,2]'),
    ("object-keys-are-sorted", '{"b":1,"a":2,"B":3}'),
    ("object-keys-sort-by-utf16-code-units", '{"\\uffff":1,"\\ud83d\\ude00":2}'),
    ("nested", '{"b":[1,{"y":2,"x":1.0}],"a":null}'),
    ("whitespace-is-dropped", ' { "a" : [ 1 , 2 ] } '),
]

CANONICAL_ERRORS = [
    ("number-too-large", "1e5000", "number-too-large"),
    ("number-too-large-negative-exponent", "1e-5000", "number-too-large"),
    ("number-exponent-without-bound", "1e99999999999999999999", "number-too-large"),
    ("number-limit-is-exceeded-by-one-character", "1e1024", "number-too-large"),
    ("number-limit-counts-the-sign", "-1e1023", "number-too-large"),
    ("number-limit-counts-leading-zeros-of-a-fraction", "1e-1023", "number-too-large"),
]

HASH_CASES = [
    ("string", "acme", "User", '"b"'),
    ("number-by-value-integer-form", "acme", "User", "42"),
    ("number-by-value-decimal-form", "acme", "User", "42.0"),
    ("number-by-value-exponent-form", "acme", "User", "4.2e1"),
    ("null", "acme", "User", "null"),
    ("object-key-order-is-irrelevant-a", "acme", "User", '{"x":1,"y":2}'),
    ("object-key-order-is-irrelevant-b", "acme", "User", '{"y":2,"x":1}'),
    ("another-tenant", "globex", "User", '"b"'),
    ("another-type", "acme", "Order", '"b"'),
    ("platform-scope", "", "User", '"b"'),
]


def write(path, document):
    path.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def main():
    vectors = Path(__file__).resolve().parent.parent / "vectors"

    cases = [{"name": n, "inputJson": text, "canonical": canonical(parse(text))} for n, text in CANONICAL_CASES]
    for n, text, code in CANONICAL_ERRORS:
        try:
            canonical(parse(text))
        except CanonicalError as e:
            assert e.code == code, (n, e.code)
        else:
            raise AssertionError(n)
        cases.append({"name": n, "inputJson": text, "error": code})
    write(vectors / "canonical.json", {
        "spec": "papuma-changes", "version": "0.1", "kind": "canonical",
        "description": "Canonical JSON (docs/spec/canonical-json.md): canonical(parse(inputJson)) must equal "
                       "'canonical', or fail with 'error'. inputJson is raw JSON text because number text matters.",
        "cases": cases})

    derive = [(t, ty) for t, ty in [("acme", "User"), ("", "User"), ("acme", "Order"), ("globex", "User"),
                                    ("", "papuma:key-check")]]
    write(vectors / "hash.json", {
        "spec": "papuma-changes", "version": "0.1", "kind": "hash",
        "description": "Hash policy v2 (ADR-025, docs/spec/canonical-json.md): key derivation, hash values and the "
                       "key-check value, for the fixed test master key.",
        "masterKeyHex": MASTER_KEY.hex(), "kid": KID, "hkdfSalt": HKDF_SALT.decode("ascii"),
        "keys": [{"tenantId": t, "typeName": ty, "tenantKeyHex": tenant_key(MASTER_KEY, t, ty).hex()} for t, ty in derive],
        "hashes": [{"name": n, "tenantId": t, "typeName": ty, "valueJson": v,
                    "canonical": canonical(parse(v)), "hash": hash_value(MASTER_KEY, KID, t, ty, v)}
                   for n, t, ty, v in HASH_CASES],
        "keyCheck": {"kid": KID, "checkValue": key_check(MASTER_KEY)}})
    print("wrote", vectors / "canonical.json", "and", vectors / "hash.json", file=sys.stderr)


if __name__ == "__main__":
    main()
