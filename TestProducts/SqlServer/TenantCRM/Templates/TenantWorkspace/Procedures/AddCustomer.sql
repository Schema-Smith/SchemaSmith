CREATE OR ALTER PROCEDURE [{{SchemaName}}].[AddCustomer]
    @CustomerName NVARCHAR(128),
    @Email NVARCHAR(256) = NULL,
    @CountryCode CHAR(2) = NULL,
    @CustomerID INT = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [{{SchemaName}}].[Customers] ([CustomerName], [Email], [CountryCode])
    VALUES (@CustomerName, @Email, @CountryCode);

    SET @CustomerID = SCOPE_IDENTITY();

    INSERT INTO [dbo].[GlobalAuditLog] ([TenantName], [EventType], [Detail])
    VALUES (N'{{SchemaName}}', N'CustomerAdded',
            N'CustomerID=' + CAST(@CustomerID AS NVARCHAR(16)) +
            N'; Name=' + @CustomerName);
END;
