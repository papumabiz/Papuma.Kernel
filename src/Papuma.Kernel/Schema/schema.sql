CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- papuma_event_feed (unified change + business event log)
CREATE TABLE papuma_event_feed (
    sequence_id    BIGSERIAL   PRIMARY KEY,
    kind           TEXT        NOT NULL CHECK (kind IN ('Change', 'Event')),
    event_id       UUID        NULL,
    scope          TEXT        NOT NULL CHECK (scope IN ('Platform', 'Tenant')),
    tenant_id      TEXT        NULL,
    entity         TEXT        NULL,
    entity_id      TEXT        NULL,
    event_type     TEXT        NOT NULL,
    version        INT         NULL,
    correlation_id TEXT        NULL,
    causation_id   TEXT        NULL,
    actor_id       TEXT        NOT NULL,
    payload        JSONB       NOT NULL,
    idempotency_key TEXT       NULL,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    redacted       BOOLEAN     NOT NULL DEFAULT FALSE,
    CONSTRAINT ck_papuma_event_feed_scope_tenant
        CHECK (
            (scope = 'Platform' AND tenant_id IS NULL)
            OR
            (scope = 'Tenant' AND tenant_id IS NOT NULL)
        )
);

CREATE INDEX idx_papuma_event_feed_sequence      ON papuma_event_feed (sequence_id);
CREATE INDEX idx_papuma_event_feed_scope_tenant_seq ON papuma_event_feed (scope, tenant_id, sequence_id);
CREATE INDEX idx_papuma_event_feed_entity_id     ON papuma_event_feed (scope, tenant_id, entity, entity_id);
CREATE INDEX idx_papuma_event_feed_event_type    ON papuma_event_feed (event_type);
CREATE INDEX idx_papuma_event_feed_not_redacted  ON papuma_event_feed (scope, tenant_id, sequence_id) WHERE redacted = FALSE;
CREATE INDEX idx_papuma_event_feed_correlation   ON papuma_event_feed (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX idx_papuma_event_feed_actor         ON papuma_event_feed (actor_id);
CREATE UNIQUE INDEX ux_papuma_event_feed_idempotency_key
    ON papuma_event_feed (scope, tenant_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;
CREATE UNIQUE INDEX ux_papuma_event_feed_event_id
    ON papuma_event_feed (event_id) WHERE event_id IS NOT NULL;

ALTER TABLE papuma_event_feed ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS scope_isolation_papuma_event_feed ON papuma_event_feed;
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

-- papuma_projection_checkpoint
CREATE TABLE papuma_projection_checkpoint (
    projection_name  TEXT   PRIMARY KEY,
    last_sequence_id BIGINT NOT NULL DEFAULT 0,
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- papuma_projection_failures
CREATE TABLE papuma_projection_failures (
    projection_name TEXT        NOT NULL,
    sequence_id     BIGINT      NOT NULL,
    event_type      TEXT        NOT NULL,
    attempts        INT         NOT NULL,
    last_error      TEXT        NOT NULL,
    next_retry_at   TIMESTAMPTZ NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (projection_name, sequence_id)
);

-- papuma_event_outbox
CREATE TABLE papuma_event_outbox (
    outbox_id      BIGSERIAL   PRIMARY KEY,
    scope          TEXT        NOT NULL CHECK (scope IN ('Platform', 'Tenant')),
    tenant_id      TEXT        NULL,
    event_id       UUID        NOT NULL,
    event_type     TEXT        NOT NULL,
    payload        JSONB       NOT NULL,
    status         TEXT        NOT NULL DEFAULT 'Pending'
                   CHECK (status IN ('Pending', 'Sent', 'Failed')),
    attempts       INT         NOT NULL DEFAULT 0
                   CHECK (attempts >= 0),
    next_retry_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error     TEXT        NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_papuma_event_outbox_scope_tenant
        CHECK (
            (scope = 'Platform' AND tenant_id IS NULL)
            OR
            (scope = 'Tenant' AND tenant_id IS NOT NULL)
        )
);

CREATE INDEX idx_papuma_event_outbox_status_retry ON papuma_event_outbox (scope, tenant_id, status, next_retry_at);

ALTER TABLE papuma_event_outbox ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS scope_isolation_papuma_event_outbox ON papuma_event_outbox;
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

-- papuma_sensitive_data_versions
CREATE TABLE IF NOT EXISTS papuma_sensitive_data_versions (
    sensitive_ref  UUID        NOT NULL,
    version        INT         NOT NULL,
    scope          TEXT        NOT NULL CHECK (scope IN ('Platform', 'Tenant')),
    tenant_id      TEXT        NULL,
    schema_version INT         NOT NULL DEFAULT 1,
    payload        JSONB       NOT NULL,
    redacted       BOOLEAN     NOT NULL DEFAULT FALSE,
    deleted        BOOLEAN     NOT NULL DEFAULT FALSE,
    legal_hold     BOOLEAN     NOT NULL DEFAULT FALSE,
    reason         TEXT        NULL,
    actor_id       TEXT        NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    superseded_at  TIMESTAMPTZ NULL,
    PRIMARY KEY (sensitive_ref, version),
    CONSTRAINT ck_papuma_sensitive_data_scope_tenant
        CHECK (
            (scope = 'Platform' AND tenant_id IS NULL)
            OR
            (scope = 'Tenant' AND tenant_id IS NOT NULL)
        )
);

CREATE INDEX idx_papuma_sensitive_scope_tenant_ref
    ON papuma_sensitive_data_versions (scope, tenant_id, sensitive_ref, version DESC);

CREATE INDEX idx_papuma_sensitive_active
    ON papuma_sensitive_data_versions (scope, tenant_id, redacted, deleted, legal_hold, created_at);

ALTER TABLE papuma_sensitive_data_versions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS scope_isolation_papuma_sensitive_data_versions ON papuma_sensitive_data_versions;
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

-- papuma_schema_version
CREATE TABLE IF NOT EXISTS papuma_schema_version (
    version    INT         PRIMARY KEY,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO papuma_schema_version (version)
VALUES (5)
ON CONFLICT (version) DO NOTHING;
