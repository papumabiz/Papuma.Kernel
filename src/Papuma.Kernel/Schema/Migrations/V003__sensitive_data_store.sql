-- Papuma.Kernel schema migration V003
-- Adds versioned storage for sensitive data references.

CREATE TABLE IF NOT EXISTS sensitive_data_versions (
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
    CONSTRAINT ck_sensitive_data_scope_tenant
        CHECK (
            (scope = 'Platform' AND tenant_id IS NULL)
            OR
            (scope = 'Tenant' AND tenant_id IS NOT NULL)
        )
);

CREATE INDEX IF NOT EXISTS idx_sensitive_scope_tenant_ref
    ON sensitive_data_versions (scope, tenant_id, sensitive_ref, version DESC);

CREATE INDEX IF NOT EXISTS idx_sensitive_active
    ON sensitive_data_versions (scope, tenant_id, redacted, deleted, legal_hold, created_at);

ALTER TABLE sensitive_data_versions ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS scope_isolation_sensitive_data_versions ON sensitive_data_versions;
CREATE POLICY scope_isolation_sensitive_data_versions ON sensitive_data_versions
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
VALUES (3)
ON CONFLICT (version) DO NOTHING;