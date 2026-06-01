CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- change_feed
CREATE TABLE change_feed (
    sequence_id    BIGSERIAL   PRIMARY KEY,
    tenant_id      TEXT        NOT NULL DEFAULT 'default',
    entity         TEXT        NOT NULL,
    entity_id      TEXT        NOT NULL,
    event_type     TEXT        NOT NULL,
    version        INT         NOT NULL DEFAULT 1,
    correlation_id TEXT        NULL,
    causation_id   TEXT        NULL,
    actor_id       TEXT        NOT NULL,
    payload        JSONB       NOT NULL,
    timestamp      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    redacted       BOOLEAN     NOT NULL DEFAULT FALSE
);

CREATE INDEX idx_change_feed_sequence      ON change_feed (sequence_id);
CREATE INDEX idx_change_feed_tenant_seq    ON change_feed (tenant_id, sequence_id);
CREATE INDEX idx_change_feed_entity_id     ON change_feed (tenant_id, entity, entity_id);
CREATE INDEX idx_change_feed_event_type    ON change_feed (event_type);
CREATE INDEX idx_change_feed_not_redacted  ON change_feed (tenant_id, sequence_id) WHERE redacted = FALSE;
CREATE INDEX idx_change_feed_correlation   ON change_feed (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX idx_change_feed_actor         ON change_feed (actor_id);

-- projection_checkpoint
CREATE TABLE projection_checkpoint (
    projection_name  TEXT   PRIMARY KEY,
    last_sequence_id BIGINT NOT NULL DEFAULT 0,
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- projection_failures
CREATE TABLE projection_failures (
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

-- business_event_log
CREATE TABLE business_event_log (
    event_id       UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id      TEXT        NOT NULL DEFAULT 'default',
    event_type     TEXT        NOT NULL,
    entity         TEXT        NULL,
    entity_id      TEXT        NULL,
    actor_id       TEXT        NOT NULL,
    correlation_id TEXT        NULL,
    causation_id   TEXT        NULL,
    payload        JSONB       NOT NULL,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    redacted       BOOLEAN     NOT NULL DEFAULT FALSE
);

CREATE INDEX idx_business_event_type        ON business_event_log (event_type);
CREATE INDEX idx_business_event_occurred_at ON business_event_log (occurred_at);
CREATE INDEX idx_business_event_entity      ON business_event_log (tenant_id, entity, entity_id);
CREATE INDEX idx_business_event_actor       ON business_event_log (actor_id);

-- event_outbox
CREATE TABLE event_outbox (
    outbox_id      BIGSERIAL   PRIMARY KEY,
    tenant_id      TEXT        NOT NULL DEFAULT 'default',
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
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_event_outbox_status_retry ON event_outbox (tenant_id, status, next_retry_at);
