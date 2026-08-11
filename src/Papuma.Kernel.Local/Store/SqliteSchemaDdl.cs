// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// The SQLite schema — same five tables and column meanings as the Postgres kernel's
/// <c>SchemaDdl</c> (<c>document</c>, <c>change</c>, <c>event</c>, <c>checkpoint</c>,
/// <c>failure</c>), adapted for a single-writer embedded store:
/// <list type="bullet">
/// <item><c>jsonb</c> columns become <c>TEXT</c> (SQLite's JSON1 functions operate on text).</item>
/// <item><c>timestamptz</c> columns become ISO-8601 <c>TEXT</c> (<see cref="DateTimeOffset"/>
///   <c>"O"</c> format — sorts correctly, round-trips exactly).</item>
/// <item>No <c>txid</c> column — the gapless-read problem it solves (ADR-010) only exists
///   with concurrent writers; a single-writer embedded store has none.</item>
/// <item>No Row Level Security — isolation stays WHERE-predicate only (<c>scope</c>/
///   <c>tenant_id</c> columns are unchanged and still filtered on explicitly).</item>
/// <item>No <c>papuma.</c> schema prefix — SQLite has no <c>CREATE SCHEMA</c>; table names
///   are unqualified since this is a dedicated, kernel-owned database file.</item>
/// </list>
/// </summary>
internal static class SqliteSchemaDdl
{
    public const string Script =
        """
        -- ── Documents: the source of truth (ADR-002) ──────────────────────────────
        CREATE TABLE IF NOT EXISTS document
        (
            scope           TEXT    NOT NULL,
            tenant_id       TEXT    NOT NULL DEFAULT '',
            document_type   TEXT    NOT NULL,
            id              TEXT    NOT NULL,
            version         INTEGER NOT NULL,
            schema_version  INTEGER NOT NULL,
            data            TEXT    NOT NULL,
            created_by      TEXT    NOT NULL DEFAULT '',
            updated_by      TEXT    NOT NULL DEFAULT '',
            created_at      TEXT    NOT NULL,
            updated_at      TEXT    NOT NULL,

            PRIMARY KEY (scope, tenant_id, document_type, id),
            CHECK (scope IN ('Platform', 'Tenant')),
            CHECK (version >= 1),
            CHECK (schema_version >= 1)
        );

        -- ── Change feed: derived, diff-only (ADR-004) ─────────────────────────────
        CREATE TABLE IF NOT EXISTS change
        (
            seq             INTEGER PRIMARY KEY AUTOINCREMENT,
            scope           TEXT    NOT NULL,
            tenant_id       TEXT    NOT NULL DEFAULT '',
            document_type   TEXT    NOT NULL,
            document_id     TEXT    NOT NULL,
            version         INTEGER NOT NULL,
            schema_version  INTEGER NOT NULL,
            operation       INTEGER NOT NULL,
            diff            TEXT    NOT NULL,
            actor_id        TEXT    NOT NULL DEFAULT '',
            metadata        TEXT    NOT NULL DEFAULT '{}',
            occurred_at     TEXT    NOT NULL,

            CHECK (scope IN ('Platform', 'Tenant')),
            CHECK (operation IN (1, 2, 3))
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_change_document_version
            ON change (scope, tenant_id, document_type, document_id, version);

        -- ── Event log: append-only facts (ADR-013) ────────────────────────────────
        CREATE TABLE IF NOT EXISTS event
        (
            seq             INTEGER PRIMARY KEY AUTOINCREMENT,
            scope           TEXT    NOT NULL,
            tenant_id       TEXT    NOT NULL DEFAULT '',
            event_type      TEXT    NOT NULL,
            payload         TEXT    NOT NULL,
            actor_id        TEXT    NOT NULL DEFAULT '',
            metadata        TEXT    NOT NULL DEFAULT '{}',
            occurred_at     TEXT    NOT NULL,

            CHECK (scope IN ('Platform', 'Tenant'))
        );

        CREATE INDEX IF NOT EXISTS ix_event_type_occurred
            ON event (event_type, occurred_at);

        -- ── Handler infrastructure (ADR-009): no tenant data ──────────────────────
        CREATE TABLE IF NOT EXISTS checkpoint
        (
            handler_name    TEXT    NOT NULL PRIMARY KEY,
            last_seq        INTEGER NOT NULL DEFAULT 0,
            updated_at      TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS failure
        (
            handler_name    TEXT    NOT NULL,
            seq             INTEGER NOT NULL,
            attempts        INTEGER NOT NULL,
            last_error      TEXT    NOT NULL,
            next_retry_at   TEXT    NOT NULL,
            updated_at      TEXT    NOT NULL,

            PRIMARY KEY (handler_name, seq)
        );
        """;
}
