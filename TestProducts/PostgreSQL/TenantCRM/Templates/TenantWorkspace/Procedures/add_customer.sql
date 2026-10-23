CREATE OR REPLACE PROCEDURE "{{SchemaName}}".add_customer(
    p_customer_name VARCHAR(128),
    p_email VARCHAR(256) DEFAULT NULL,
    p_country_code CHAR(2) DEFAULT NULL,
    INOUT p_customer_id INTEGER DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO "{{SchemaName}}".customers (customer_name, email, country_code)
    VALUES (p_customer_name, p_email, p_country_code)
    RETURNING customer_id INTO p_customer_id;

    INSERT INTO public.global_audit_log (tenant_name, event_type, detail)
    VALUES ('{{SchemaName}}', 'CustomerAdded',
            'customer_id=' || p_customer_id::TEXT || '; name=' || p_customer_name);
END;
$$;
