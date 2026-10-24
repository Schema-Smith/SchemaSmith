CREATE OR REPLACE FUNCTION "{{SchemaName}}".get_customer_lifetime_value(p_customer_id INTEGER)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    activity_count INTEGER;
BEGIN
    -- Toy lifetime-value metric: count of activities recorded against the customer.
    -- A real implementation would join orders / invoices and sum values, but the
    -- demo's point is that per-tenant functions use {{SchemaName}}-qualified refs
    -- so each tenant's function reads its own data.
    SELECT COUNT(*) INTO activity_count
      FROM "{{SchemaName}}".activities
     WHERE customer_id = p_customer_id;

    RETURN COALESCE(activity_count, 0);
END;
$$;
