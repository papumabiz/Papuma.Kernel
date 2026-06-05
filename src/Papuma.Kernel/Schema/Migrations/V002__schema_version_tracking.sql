-- Papuma.Kernel schema migration V002
-- Adds explicit schema version tracking for operational checks.

CREATE TABLE IF NOT EXISTS papuma_schema_version (
    version    INT         PRIMARY KEY,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO papuma_schema_version (version)
VALUES (2)
ON CONFLICT (version) DO NOTHING;
