-- Papuma.Kernel schema migration V006
-- Extends all RLS policies with an 'All' scope condition so that background workers
-- using ScopeFilter.All() can read across scopes without requiring BYPASSRLS.
-- The worker sets SET LOCAL app.current_scope = 'All' inside its transaction;
-- the policy then grants unrestricted read access while still blocking connections
-- that carry no scope at all (NULL or empty string).

-- ── papuma_event_feed ──
DROP POLICY IF EXISTS scope_isolation_papuma_event_feed ON papuma_event_feed;
CREATE POLICY scope_isolation_papuma_event_feed ON papuma_event_feed
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

-- ── papuma_event_outbox ──
DROP POLICY IF EXISTS scope_isolation_papuma_event_outbox ON papuma_event_outbox;
CREATE POLICY scope_isolation_papuma_event_outbox ON papuma_event_outbox
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

-- ── papuma_sensitive_data_versions ──
DROP POLICY IF EXISTS scope_isolation_papuma_sensitive_data_versions ON papuma_sensitive_data_versions;
CREATE POLICY scope_isolation_papuma_sensitive_data_versions ON papuma_sensitive_data_versions
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

INSERT INTO papuma_schema_version (version)
VALUES (6)
ON CONFLICT (version) DO NOTHING;
