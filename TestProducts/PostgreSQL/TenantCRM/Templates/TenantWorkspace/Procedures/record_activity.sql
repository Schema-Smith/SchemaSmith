CREATE OR REPLACE PROCEDURE "{{SchemaName}}".record_activity(
    p_customer_id INTEGER,
    p_activity_type_id INTEGER,
    p_note VARCHAR(1024) DEFAULT NULL,
    INOUT p_activity_id BIGINT DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO "{{SchemaName}}".activities (customer_id, activity_type_id, note)
    VALUES (p_customer_id, p_activity_type_id, p_note)
    RETURNING activity_id INTO p_activity_id;

    INSERT INTO public.global_audit_log (tenant_name, event_type, detail)
    VALUES ('{{SchemaName}}', 'ActivityRecorded',
            'customer_id=' || p_customer_id::TEXT || '; type_id=' || p_activity_type_id::TEXT);
END;
$$;
