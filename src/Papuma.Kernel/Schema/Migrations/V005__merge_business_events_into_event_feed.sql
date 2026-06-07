-- Papuma.Kernel schema migration V005
-- 1. Merges business_event_log into change_feed, creating a unified papuma_event_feed table.
-- 2. Adds papuma_ prefix to all framework tables for clear namespacing in host databases.

-- ── Rename change_feed to papuma_event_feed ──
ALTER TABLE change_feed RENAME TO papuma_event_feed;
ALTER INDEX IF EXISTS ux_change_feed_idempotency_key RENAME TO ux_papuma_event_feed_idempotency_key;
ALTER INDEX IF EXISTS idx_change_feed_sequence RENAME TO idx_papuma_event_feed_sequence;
ALTER INDEX IF EXISTS idx_change_feed_scope_tenant_seq RENAME TO idx_papuma_event_feed_scope_tenant_seq;
ALTER INDEX IF EXISTS idx_change_feed_entity_id RENAME TO idx_papuma_event_feed_entity_id;
ALTER INDEX IF EXISTS idx_change_feed_event_type RENAME TO idx_papuma_event_feed_event_type;
ALTER INDEX IF EXISTS idx_change_feed_not_redacted RENAME TO idx_papuma_event_feed_not_redacted;
ALTER INDEX IF EXISTS idx_change_feed_correlation RENAME TO idx_papuma_event_feed_correlation;
ALTER INDEX IF EXISTS idx_change_feed_actor RENAME TO idx_papuma_event_feed_actor;

-- ── Add new columns to papuma_event_feed ──
ALTER TABLE papuma_event_feed ADD COLUMN IF NOT EXISTS event_id UUID NULL;
ALTER TABLE papuma_event_feed ADD COLUMN IF NOT EXISTS kind TEXT NOT NULL DEFAULT 'Change';

-- Make entity / entity_id nullable.
ALTER TABLE papuma_event_feed ALTER COLUMN entity DROP NOT NULL;
ALTER TABLE papuma_event_feed ALTER COLUMN entity_id DROP NOT NULL;

-- Make version nullable.
ALTER TABLE papuma_event_feed ALTER COLUMN version DROP NOT NULL;

-- Rename timestamp to occurred_at.
ALTER TABLE papuma_event_feed RENAME COLUMN timestamp TO occurred_at;

-- Unique index on event_id for Outbox correlation.
CREATE UNIQUE INDEX IF NOT EXISTS ux_papuma_event_feed_event_id
    ON papuma_event_feed (event_id) WHERE event_id IS NOT NULL;

-- ── Backfill business_event_log into papuma_event_feed ──
INSERT INTO papuma_event_feed
    (event_id, kind, scope, tenant_id, entity, entity_id, event_type,
     version, correlation_id, causation_id, actor_id, payload, occurred_at,
     redacted, idempotency_key)
SELECT
    event_id, 'Event', scope, tenant_id, entity, entity_id, event_type,
    NULL, correlation_id, causation_id, actor_id, payload, occurred_at,
    redacted, idempotency_key
FROM business_event_log;

-- Drop the now-obsolete business_event_log.
DROP TABLE business_event_log CASCADE;

-- ── Rename constraint on papuma_event_feed ──
ALTER TABLE papuma_event_feed RENAME CONSTRAINT ck_change_feed_scope_tenant TO ck_papuma_event_feed_scope_tenant;

-- ── RLS for papuma_event_feed ──
ALTER TABLE papuma_event_feed ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS scope_isolation_change_feed ON papuma_event_feed;
CREATE POLICY scope_isolation_papuma_event_feed ON papuma_event_feed
    USING (
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

-- ── Rename projection_checkpoint → papuma_projection_checkpoint ──
ALTER TABLE projection_checkpoint RENAME TO papuma_projection_checkpoint;

-- ── Rename projection_failures → papuma_projection_failures ──
ALTER TABLE projection_failures RENAME TO papuma_projection_failures;

-- ── Rename event_outbox → papuma_event_outbox ──
ALTER TABLE event_outbox RENAME TO papuma_event_outbox;
ALTER INDEX IF EXISTS idx_event_outbox_status_retry RENAME TO idx_papuma_event_outbox_status_retry;
ALTER TABLE papuma_event_outbox RENAME CONSTRAINT ck_event_outbox_scope_tenant TO ck_papuma_event_outbox_scope_tenant;

ALTER TABLE papuma_event_outbox ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS scope_isolation_event_outbox ON papuma_event_outbox;
CREATE POLICY scope_isolation_papuma_event_outbox ON papuma_event_outbox
    USING (
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

-- ── Rename sensitive_data_versions → papuma_sensitive_data_versions ──
ALTER TABLE sensitive_data_versions RENAME TO papuma_sensitive_data_versions;
ALTER INDEX IF EXISTS idx_sensitive_scope_tenant_ref RENAME TO idx_papuma_sensitive_scope_tenant_ref;
ALTER INDEX IF EXISTS idx_sensitive_active RENAME TO idx_papuma_sensitive_active;
ALTER TABLE papuma_sensitive_data_versions RENAME CONSTRAINT ck_sensitive_data_scope_tenant TO ck_papuma_sensitive_data_scope_tenant;

ALTER TABLE papuma_sensitive_data_versions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS scope_isolation_sensitive_data_versions ON papuma_sensitive_data_versions;
CREATE POLICY scope_isolation_papuma_sensitive_data_versions ON papuma_sensitive_data_versions
    USING (
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

-- ── Rename papuma_schema_version (already has prefix, ensure table exists) ──
ALTER TABLE IF EXISTS papuma_schema_version RENAME TO papuma_schema_version;

INSERT INTO papuma_schema_version (version)
VALUES (5)
ON CONFLICT (version) DO NOTHING;
