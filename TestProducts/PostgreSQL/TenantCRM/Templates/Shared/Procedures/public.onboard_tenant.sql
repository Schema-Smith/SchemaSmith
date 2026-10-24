CREATE OR REPLACE PROCEDURE public.onboard_tenant(
    p_name VARCHAR(64),
    p_display_name VARCHAR(128),
    p_plan_id INTEGER
)
LANGUAGE plpgsql
AS $$
BEGIN
    -- Canonical onboarding pattern: CREATE SCHEMA + INSERT atomically. Pairs with
    -- TenantWorkspace's CreateSchemaIfMissing=false setting so that discovery of
    -- the tenant row is what authorizes the schema iteration on the next quench.

    IF EXISTS (SELECT 1 FROM public.tenants WHERE name = p_name) THEN
        RAISE EXCEPTION 'Tenant % already exists in public.tenants.', p_name;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = p_name) THEN
        RAISE EXCEPTION 'Schema "%" already exists. Cannot onboard tenant.', p_name;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM public.plans WHERE plan_id = p_plan_id) THEN
        RAISE EXCEPTION 'plan_id % does not exist in public.plans.', p_plan_id;
    END IF;

    -- Procedures support implicit transactions in plpgsql; both DDL and DML
    -- below land atomically if the caller didn't open its own transaction.
    EXECUTE format('CREATE SCHEMA %I', p_name);

    INSERT INTO public.tenants (name, display_name, plan_id)
    VALUES (p_name, p_display_name, p_plan_id);

    INSERT INTO public.global_audit_log (tenant_name, event_type, detail)
    VALUES (p_name, 'Onboarded', 'Plan=' || p_plan_id::TEXT);
END;
$$;
