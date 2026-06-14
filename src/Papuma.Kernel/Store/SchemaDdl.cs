// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// Holds the idempotent DDL script for all kernel tables (architecture §4).
/// </summary>
/// <remarks>
/// <para>
/// Tenant-bearing tables (<c>papuma.document</c>, <c>papuma.change</c>) carry the
/// two-layer security model: explicit scope predicates in all kernel SQL (layer 1)
/// plus Row Level Security policies reading <c>app.current_scope</c> /
/// <c>app.current_tenant</c> (layer 2, see <see cref="Tenancy.ScopeConnectionExtensions"/>).
/// <c>FORCE ROW LEVEL SECURITY</c> subjects the table owner as well; only superusers
/// bypass RLS.
/// </para>
/// <para>
/// <c>papuma.checkpoint</c> and <c>papuma.failure</c> hold handler infrastructure
/// without tenant data and therefore carry no RLS.
/// </para>
/// </remarks>
internal static class SchemaDdl
{
    /// <summary>
    /// The complete idempotent schema script. Safe to execute repeatedly.
    /// </summary>
    public const string Script = """
        CREATE SCHEMA IF NOT EXISTS papuma;

        -- ── Documents: the source of truth (ADR-002) ──────────────────────────────
        CREATE TABLE IF NOT EXISTS papuma.document
        (
            scope           text        NOT NULL,
            tenant_id       text        NOT NULL DEFAULT '',
            document_type   text        NOT NULL,
            id              text        NOT NULL,
            version         bigint      NOT NULL,
            schema_version  int         NOT NULL,
            data            jsonb       NOT NULL,
            created_at      timestamptz NOT NULL DEFAULT now(),
            updated_at      timestamptz NOT NULL DEFAULT now(),

            PRIMARY KEY (scope, tenant_id, document_type, id),
            CONSTRAINT ck_document_scope CHECK (scope IN ('Platform', 'Tenant')),
            CONSTRAINT ck_document_version CHECK (version >= 1),
            CONSTRAINT ck_document_schema_version CHECK (schema_version >= 1)
        );

        -- ── Change feed: derived, diff-only (ADR-004), txid for gapless reads (ADR-010) ──
        CREATE TABLE IF NOT EXISTS papuma.change
        (
            seq             bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            scope           text        NOT NULL,
            tenant_id       text        NOT NULL DEFAULT '',
            document_type   text        NOT NULL,
            document_id     text        NOT NULL,
            version         bigint      NOT NULL,
            schema_version  int         NOT NULL,
            operation       smallint    NOT NULL,
            diff            jsonb       NOT NULL,
            metadata        jsonb       NOT NULL DEFAULT '{}'::jsonb,
            occurred_at     timestamptz NOT NULL DEFAULT now(),
            txid            xid8        NOT NULL DEFAULT pg_current_xact_id(),

            CONSTRAINT ck_change_scope CHECK (scope IN ('Platform', 'Tenant')),
            CONSTRAINT ck_change_operation CHECK (operation IN (1, 2, 3))
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_papuma_change_document_version
            ON papuma.change (scope, tenant_id, document_type, document_id, version);

        -- ── Event log: append-only facts (ADR-013), txid for gapless reads ────────
        CREATE TABLE IF NOT EXISTS papuma.event
        (
            seq             bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            scope           text        NOT NULL,
            tenant_id       text        NOT NULL DEFAULT '',
            event_type      text        NOT NULL,
            payload         jsonb       NOT NULL,
            metadata        jsonb       NOT NULL DEFAULT '{}'::jsonb,
            occurred_at     timestamptz NOT NULL DEFAULT now(),
            txid            xid8        NOT NULL DEFAULT pg_current_xact_id(),

            CONSTRAINT ck_event_scope CHECK (scope IN ('Platform', 'Tenant'))
        );

        CREATE INDEX IF NOT EXISTS ix_papuma_event_type_occurred
            ON papuma.event (event_type, occurred_at);

        -- ── Handler infrastructure (ADR-009): no tenant data, no RLS ──────────────
        CREATE TABLE IF NOT EXISTS papuma.checkpoint
        (
            handler_name    text        NOT NULL PRIMARY KEY,
            last_seq        bigint      NOT NULL DEFAULT 0,
            updated_at      timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS papuma.failure
        (
            handler_name    text        NOT NULL,
            seq             bigint      NOT NULL,
            attempts        int         NOT NULL,
            last_error      text        NOT NULL,
            next_retry_at   timestamptz NOT NULL,
            updated_at      timestamptz NOT NULL DEFAULT now(),

            PRIMARY KEY (handler_name, seq)
        );

        -- ── Row Level Security (layer 2 of the two-layer scope model) ─────────────
        ALTER TABLE papuma.document ENABLE ROW LEVEL SECURITY;
        ALTER TABLE papuma.document FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS scope_isolation_document ON papuma.document;
        CREATE POLICY scope_isolation_document ON papuma.document
            USING (
                current_setting('app.current_scope', true) = 'All'
                OR
                (
                    current_setting('app.current_scope', true) = 'Tenant'
                    AND scope = 'Tenant'
                    AND tenant_id = current_setting('app.current_tenant', true)
                )
                OR
                (
                    current_setting('app.current_scope', true) = 'Platform'
                    AND scope = 'Platform'
                )
            )
            WITH CHECK (
                (
                    current_setting('app.current_scope', true) = 'Tenant'
                    AND scope = 'Tenant'
                    AND tenant_id = current_setting('app.current_tenant', true)
                )
                OR
                (
                    current_setting('app.current_scope', true) = 'Platform'
                    AND scope = 'Platform'
                )
            );

        ALTER TABLE papuma.event ENABLE ROW LEVEL SECURITY;
        ALTER TABLE papuma.event FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS scope_isolation_event ON papuma.event;
        CREATE POLICY scope_isolation_event ON papuma.event
            USING (
                current_setting('app.current_scope', true) = 'All'
                OR
                (
                    current_setting('app.current_scope', true) = 'Tenant'
                    AND scope = 'Tenant'
                    AND tenant_id = current_setting('app.current_tenant', true)
                )
                OR
                (
                    current_setting('app.current_scope', true) = 'Platform'
                    AND scope = 'Platform'
                )
            )
            WITH CHECK (
                (
                    current_setting('app.current_scope', true) = 'Tenant'
                    AND scope = 'Tenant'
                    AND tenant_id = current_setting('app.current_tenant', true)
                )
                OR
                (
                    current_setting('app.current_scope', true) = 'Platform'
                    AND scope = 'Platform'
                )
            );

        ALTER TABLE papuma.change ENABLE ROW LEVEL SECURITY;
        ALTER TABLE papuma.change FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS scope_isolation_change ON papuma.change;
        CREATE POLICY scope_isolation_change ON papuma.change
            USING (
                current_setting('app.current_scope', true) = 'All'
                OR
                (
                    current_setting('app.current_scope', true) = 'Tenant'
                    AND scope = 'Tenant'
                    AND tenant_id = current_setting('app.current_tenant', true)
                )
                OR
                (
                    current_setting('app.current_scope', true) = 'Platform'
                    AND scope = 'Platform'
                )
            )
            WITH CHECK (
                (
                    current_setting('app.current_scope', true) = 'Tenant'
                    AND scope = 'Tenant'
                    AND tenant_id = current_setting('app.current_tenant', true)
                )
                OR
                (
                    current_setting('app.current_scope', true) = 'Platform'
                    AND scope = 'Platform'
                )
            );
        """;
}
