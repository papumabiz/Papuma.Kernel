-- Papuma.Kernel schema migration V005
-- Merges business_event_log into change_feed, creating a unified event_feed table.

-- Rename existing table and its indexes to reflect the unified purpose.
ALTER TABLE change_feed RENAME TO event_feed;
ALTER INDEX IF EXISTS ux_change_feed_idempotency_key RENAME TO ux_event_feed_idempotency_key;
ALTER INDEX IF EXISTS idx_change_feed_sequence RENAME TO idx_event_feed_sequence;
ALTER INDEX IF EXISTS idx_change_feed_scope_tenant_seq RENAME TO idx_event_feed_scope_tenant_seq;
ALTER INDEX IF EXISTS idx_change_feed_entity_id RENAME TO idx_event_feed_entity_id;
ALTER INDEX IF EXISTS idx_change_feed_event_type RENAME TO idx_event_feed_event_type;
ALTER INDEX IF EXISTS idx_change_feed_not_redacted RENAME TO idx_event_feed_not_redacted;
ALTER INDEX IF EXISTS idx_change_feed_correlation RENAME TO idx_event_feed_correlation;
ALTER INDEX IF EXISTS idx_change_feed_actor RENAME TO idx_event_feed_actor;

-- Add new columns: event_id for UUID-based event identity (used by Outbox), kind discriminator.
ALTER TABLE event_feed ADD COLUMN IF NOT EXISTS event_id UUID NULL;
ALTER TABLE event_feed ADD COLUMN IF NOT EXISTS kind TEXT NOT NULL DEFAULT 'Change';

-- Make entity / entity_id nullable: business events may not be bound to a specific entity.
ALTER TABLE event_feed ALTER COLUMN entity DROP NOT NULL;
ALTER TABLE event_feed ALTER COLUMN entity_id DROP NOT NULL;

-- Make version nullable: business events are not versioned.
ALTER TABLE event_feed ALTER COLUMN version DROP NOT NULL;

-- Rename timestamp to occurred_at for consistency across event types.
ALTER TABLE event_feed RENAME COLUMN timestamp TO occurred_at;

-- Unique index on event_id for Outbox correlation.
CREATE UNIQUE INDEX IF NOT EXISTS ux_event_feed_event_id
    ON event_feed (event_id) WHERE event_id IS NOT NULL;

-- Backfill existing business_event_log rows into the unified table.
INSERT INTO event_feed
    (event_id, kind, scope, tenant_id, entity, entity_id, event_type,
     version, correlation_id, causation_id, actor_id, payload, occurred_at,
     redacted, idempotency_key)
SELECT
    event_id, 'Event', scope, tenant_id, entity, entity_id, event_type,
    NULL, correlation_id, causation_id, actor_id, payload, occurred_at,
    redacted, idempotency_key
FROM business_event_log;

-- Drop the now-obsolete business_event_log table and its indexes.
DROP TABLE business_event_log CASCADE;

-- Update RLS policies for the renamed table.
ALTER TABLE event_feed ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS scope_isolation_change_feed ON event_feed;
CREATE POLICY scope_isolation_event_feed ON event_feed
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

INSERT INTO papuma_schema_version (version)
VALUES (5)
ON CONFLICT (version) DO NOTHING;
