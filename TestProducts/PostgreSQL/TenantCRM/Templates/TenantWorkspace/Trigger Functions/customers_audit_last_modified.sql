CREATE OR REPLACE FUNCTION "{{SchemaName}}".customers_audit_last_modified()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.last_modified_at := now();
    RETURN NEW;
END;
$$;
