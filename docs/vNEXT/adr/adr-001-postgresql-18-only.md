# ADR-001: PostgreSQL ≥ 18 als einzige Zieldatenbank

## Status

Accepted (2026-06-11)

## Kontext

Die ursprüngliche vNEXT-Diskussion (chat-1.md) skizzierte eine DB-agnostische
Core-Library mit Provider-Paketen (`Papuma.Postgres`, `Papuma.SqlServer`, ...).
Der Kernel lebt jedoch von Features, die nur PostgreSQL in dieser Kombination bietet:

- **JSONB** mit Operatoren und Expression-Indizes (Keys/Constraints auf Dokumentfeldern),
- **`RETURNING OLD/NEW`** (ab PostgreSQL 18): alter und neuer Zustand atomar in einem
  Statement — Fundament des Write-Pfads (ADR-003),
- **LISTEN/NOTIFY** als Wakeup für den Feed-Konsum (ADR-010),
- **`pg_current_xact_id()` / Snapshot-Funktionen** für lückenloses Feed-Lesen (ADR-010).

Eine Provider-Abstraktion müsste all das auf den kleinsten gemeinsamen Nenner
herunterbrechen oder pro Provider Sonderpfade pflegen — beides verwässert das Design,
bevor es einen einzigen Nutzer für einen zweiten Provider gibt.

## Entscheidung

1. vNEXT unterstützt ausschließlich **PostgreSQL, Mindestversion 18**.
2. Es gibt **kein `IStorageProvider`-Interface** und keine Provider-Pakete.
   SQL liegt direkt im Kernel.
3. Die Mindestversion wird beim Start geprüft (`SHOW server_version_num`, < 180000 →
   aussagekräftige Exception).

## Konsequenzen

- Der Write-Pfad darf sich vorbehaltlos auf `RETURNING OLD/NEW` stützen; kein
  Fallback-Codepfad "altes Dokument vorher laden".
- Wer eine andere Datenbank braucht, braucht ein anderes Produkt — das ist eine bewusste
  Positionierung, keine Lücke.
- Sollte später doch Bedarf entstehen, ist das Extrahieren einer Abstraktion aus einer
  funktionierenden Postgres-Implementierung leichter als das umgekehrte Vorgehen.
