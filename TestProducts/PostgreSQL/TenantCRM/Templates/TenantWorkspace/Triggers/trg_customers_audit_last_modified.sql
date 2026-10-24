DROP TRIGGER IF EXISTS trg_customers_audit_last_modified ON "{{SchemaName}}".customers;
CREATE TRIGGER trg_customers_audit_last_modified
    BEFORE UPDATE ON "{{SchemaName}}".customers
    FOR EACH ROW
    EXECUTE FUNCTION "{{SchemaName}}".customers_audit_last_modified();
