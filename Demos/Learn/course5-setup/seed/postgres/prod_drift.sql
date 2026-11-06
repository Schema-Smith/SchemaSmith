-- Production-only drift (PostgreSQL), applied on top of shop.sql to shop_brownfield_prod.
-- Each object is what years of hotfixes leave behind in production and never in dev.
DROP TABLE IF EXISTS reportcache;
ALTER TABLE customer ADD COLUMN loyaltytier VARCHAR(10) NULL;
ALTER TABLE product ADD CONSTRAINT ck_product_legacysku CHECK (length(sku) <= 12);
CREATE INDEX ix_salesorder_status ON salesorder (status);
CREATE TABLE reportcache (
  reportname VARCHAR(100) NOT NULL CONSTRAINT pk_reportcache PRIMARY KEY,
  payload    TEXT NULL
);
INSERT INTO customer (customerid, email, fullname, loyaltytier) VALUES
  (1, 'ada@example.com', 'Ada Lovelace', 'GOLD'),
  (2, 'alan@example.com', 'Alan Turing', 'SILVER');
INSERT INTO reportcache (reportname, payload) VALUES ('daily-sales', '{"total": 1250.00}');
