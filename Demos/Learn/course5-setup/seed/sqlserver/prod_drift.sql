-- Production-only drift (SQL Server), applied on top of shop.sql to shop_brownfield_prod.
-- Each object is what years of hotfixes leave behind in production and never in dev.
IF OBJECT_ID('dbo.ReportCache') IS NOT NULL DROP TABLE dbo.ReportCache;
ALTER TABLE dbo.Customer ADD LoyaltyTier VARCHAR(10) NULL;
ALTER TABLE dbo.Product ADD CONSTRAINT CK_Product_LegacySku CHECK (LEN(Sku) <= 12);
CREATE INDEX IX_SalesOrder_Status ON dbo.SalesOrder (Status);
CREATE TABLE dbo.ReportCache (
  ReportName VARCHAR(100) NOT NULL CONSTRAINT PK_ReportCache PRIMARY KEY,
  Payload    NVARCHAR(MAX) NULL
);
GO
INSERT INTO dbo.Customer (CustomerId, Email, FullName, LoyaltyTier) VALUES
  (1, 'ada@example.com', 'Ada Lovelace', 'GOLD'),
  (2, 'alan@example.com', 'Alan Turing', 'SILVER');
INSERT INTO dbo.ReportCache (ReportName, Payload) VALUES ('daily-sales', N'{"total": 1250.00}');
GO
