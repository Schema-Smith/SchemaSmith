-- Production-only drift (MariaDB), applied on top of shop.sql to shop_brownfield_prod.
-- Each object is what years of hotfixes leave behind in production and never in dev.
DROP TABLE IF EXISTS ReportCache;
ALTER TABLE Customer ADD COLUMN LoyaltyTier VARCHAR(10) NULL;
ALTER TABLE Product ADD CONSTRAINT CK_Product_LegacySku CHECK (CHAR_LENGTH(Sku) <= 12);
CREATE INDEX IX_SalesOrder_Status ON SalesOrder (Status);
CREATE TABLE ReportCache (
  ReportName VARCHAR(100) NOT NULL,
  Payload    TEXT NULL,
  CONSTRAINT PK_ReportCache PRIMARY KEY (ReportName)
) ENGINE=InnoDB;
INSERT INTO Customer (CustomerId, Email, FullName, LoyaltyTier) VALUES
  (1, 'ada@example.com', 'Ada Lovelace', 'GOLD'),
  (2, 'alan@example.com', 'Alan Turing', 'SILVER');
INSERT INTO ReportCache (ReportName, Payload) VALUES ('daily-sales', '{"total": 1250.00}');
