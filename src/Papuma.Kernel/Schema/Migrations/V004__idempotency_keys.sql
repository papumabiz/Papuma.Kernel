-- Papuma.Kernel schema migration V004
-- Adds idempotency keys for change feed and business event log writes.

ALTER TABLE change_feed
    ADD COLUMN IF NOT EXISTS idempotency_key TEXT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_change_feed_idempotency_key
    ON change_feed (scope, tenant_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

ALTER TABLE business_event_log
    ADD COLUMN IF NOT EXISTS idempotency_key TEXT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_business_event_log_idempotency_key
    ON business_event_log (scope, tenant_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

INSERT INTO papuma_schema_version (version)
VALUES (4)
ON CONFLICT (version) DO NOTHING;