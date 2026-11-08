-- The security layer a DBA built by hand in declared_m11: row-level security, a signed procedure,
-- and triggers whose state and order matter. Re-runnable: everything is dropped first.
IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = 'RegionPolicy') DROP SECURITY POLICY Security.RegionPolicy;
IF OBJECT_ID('dbo.usp_RegionTotals') IS NOT NULL DROP PROCEDURE dbo.usp_RegionTotals;
IF OBJECT_ID('dbo.OrderAudit') IS NOT NULL DROP TABLE dbo.OrderAudit;
IF OBJECT_ID('dbo.Orders') IS NOT NULL DROP TABLE dbo.Orders;
IF OBJECT_ID('Security.fn_RegionFilter') IS NOT NULL DROP FUNCTION Security.fn_RegionFilter;
IF OBJECT_ID('dbo.Customer') IS NOT NULL DROP TABLE dbo.Customer;
IF EXISTS (SELECT 1 FROM sys.column_encryption_keys WHERE name = 'CustomerCEK') DROP COLUMN ENCRYPTION KEY CustomerCEK;
IF EXISTS (SELECT 1 FROM sys.column_master_keys WHERE name = 'CustomerCMK') DROP COLUMN MASTER KEY CustomerCMK;
IF EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'ReportSigning') DROP CERTIFICATE ReportSigning;
GO
IF SCHEMA_ID('Security') IS NULL EXEC('CREATE SCHEMA Security');
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
  CREATE MASTER KEY ENCRYPTION BY PASSWORD = 'Learn!Passw0rd-dmk';
GO
CREATE COLUMN MASTER KEY CustomerCMK WITH (
  KEY_STORE_PROVIDER_NAME = N'MSSQL_CERTIFICATE_STORE',
  KEY_PATH = N'CurrentUser/My/A66BB0F6DD70BDFF02B62D0F87E340288E6F9305');
CREATE COLUMN ENCRYPTION KEY CustomerCEK WITH VALUES (
  COLUMN_MASTER_KEY = CustomerCMK, ALGORITHM = 'RSA_OAEP',
  ENCRYPTED_VALUE = 0x016E000001630075007200720065006E0074007500730065007200);
GO
CREATE TABLE dbo.Customer (
  CustomerId INT NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
  SSN NVARCHAR(11) COLLATE Latin1_General_BIN2
    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CustomerCEK, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NULL
);
CREATE TABLE dbo.Orders (
  OrderId INT NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
  Region  NVARCHAR(10) NOT NULL,
  Amount  DECIMAL(10,2) NOT NULL
);
CREATE TABLE dbo.OrderAudit (
  AuditId INT IDENTITY NOT NULL CONSTRAINT PK_OrderAudit PRIMARY KEY,
  OrderId INT NOT NULL,
  Note    NVARCHAR(50) NOT NULL
);
GO
CREATE FUNCTION Security.fn_RegionFilter(@Region NVARCHAR(10))
RETURNS TABLE WITH SCHEMABINDING
AS RETURN SELECT 1 AS Allowed
WHERE @Region = CAST(SESSION_CONTEXT(N'Region') AS NVARCHAR(10)) OR SESSION_CONTEXT(N'Region') IS NULL;
GO
CREATE SECURITY POLICY Security.RegionPolicy
  ADD FILTER PREDICATE Security.fn_RegionFilter(Region) ON dbo.Orders
  WITH (STATE = ON);
GO
CREATE PROCEDURE dbo.usp_RegionTotals AS
SELECT Region, SUM(Amount) AS Total FROM dbo.Orders GROUP BY Region;
GO
CREATE CERTIFICATE ReportSigning WITH SUBJECT = 'Signs the reporting procedures';
ADD SIGNATURE TO dbo.usp_RegionTotals BY CERTIFICATE ReportSigning;
GO
CREATE TRIGGER dbo.trg_Orders_Stamp ON dbo.Orders AFTER INSERT AS
INSERT dbo.OrderAudit (OrderId, Note) SELECT OrderId, N'stamped' FROM inserted;
GO
CREATE TRIGGER dbo.trg_Orders_Notify ON dbo.Orders AFTER INSERT AS
INSERT dbo.OrderAudit (OrderId, Note) SELECT OrderId, N'notified' FROM inserted;
GO
CREATE TRIGGER dbo.trg_Orders_Legacy ON dbo.Orders AFTER INSERT AS
INSERT dbo.OrderAudit (OrderId, Note) SELECT OrderId, N'legacy' FROM inserted;
GO
EXEC sp_settriggerorder @triggername = 'dbo.trg_Orders_Stamp', @order = 'First', @stmttype = 'INSERT';
DISABLE TRIGGER dbo.trg_Orders_Legacy ON dbo.Orders;
GO
INSERT dbo.Orders (OrderId, Region, Amount) VALUES (1, N'EU', 100), (2, N'US', 250), (3, N'EU', 75);
GO
