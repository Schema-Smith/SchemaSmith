CREATE OR ALTER FUNCTION [{{SchemaName}}].[GetCustomerLifetimeValue](@CustomerID INT)
RETURNS INT
AS
BEGIN
    -- Toy lifetime-value metric: count of activities recorded against the customer.
    -- A real implementation would join Orders / Invoices and sum values, but the
    -- demo's point is that per-tenant functions use {{SchemaName}}-qualified refs
    -- so each tenant's function reads its own data.
    DECLARE @ActivityCount INT;

    SELECT @ActivityCount = COUNT(*)
      FROM [{{SchemaName}}].[Activities]
     WHERE [CustomerID] = @CustomerID;

    RETURN ISNULL(@ActivityCount, 0);
END;
