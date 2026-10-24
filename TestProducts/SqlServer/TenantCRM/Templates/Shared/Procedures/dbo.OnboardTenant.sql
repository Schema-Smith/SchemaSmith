CREATE OR ALTER PROCEDURE [dbo].[OnboardTenant]
    @Name NVARCHAR(64),
    @DisplayName NVARCHAR(128),
    @PlanID INT
AS
BEGIN
    SET NOCOUNT ON;

    -- Canonical onboarding pattern: CREATE SCHEMA + INSERT atomically. Pairs with
    -- TenantWorkspace's CreateSchemaIfMissing=false setting so that discovery of
    -- the tenant row is what authorizes the schema iteration on the next quench.
    -- A tenant row that exists without its schema (or vice versa) is the bug class
    -- this pattern prevents.

    -- Cheap pre-checks outside the transaction.
    IF EXISTS (SELECT 1 FROM dbo.Tenants WHERE [Name] = @Name)
    BEGIN
        RAISERROR(N'Tenant %s already exists in dbo.Tenants.', 16, 1, @Name);
        RETURN;
    END

    IF SCHEMA_ID(@Name) IS NOT NULL
    BEGIN
        RAISERROR(N'Schema [%s] already exists. Cannot onboard tenant.', 16, 1, @Name);
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Plans WHERE [PlanID] = @PlanID)
    BEGIN
        RAISERROR(N'PlanID %d does not exist in dbo.Plans.', 16, 1, @PlanID);
        RETURN;
    END

    BEGIN TRY
        BEGIN TRANSACTION;

        -- CREATE SCHEMA must be its own batch in normal scripts, but inside a procedure
        -- we use dynamic SQL so it sits in this transaction with the INSERT.
        DECLARE @ddl NVARCHAR(200) = N'CREATE SCHEMA ' + QUOTENAME(@Name);
        EXEC sp_executesql @ddl;

        INSERT INTO dbo.Tenants ([Name], [DisplayName], [PlanID])
        VALUES (@Name, @DisplayName, @PlanID);

        INSERT INTO dbo.GlobalAuditLog ([TenantName], [EventType], [Detail])
        VALUES (@Name, N'Onboarded', N'Plan=' + CAST(@PlanID AS NVARCHAR(16)));

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END;
