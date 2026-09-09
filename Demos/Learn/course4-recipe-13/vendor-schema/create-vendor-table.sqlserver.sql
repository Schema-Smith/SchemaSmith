CREATE TABLE dbo.vendor_order (
  order_id      INT           NOT NULL PRIMARY KEY,
  customer_ref  NVARCHAR(64)  NOT NULL,
  placed_at     DATETIME2     NOT NULL,
  status        NVARCHAR(32)  NOT NULL
);
