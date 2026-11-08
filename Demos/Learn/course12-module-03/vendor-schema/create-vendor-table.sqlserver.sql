-- Stands in for a vendor product creating its own table outside the package.
-- Guarded so re-running Step 1 (or resetting the lab) is safe.
IF OBJECT_ID('dbo.vendor_order') IS NULL
CREATE TABLE dbo.vendor_order (
  order_id      INT           NOT NULL PRIMARY KEY,
  customer_ref  NVARCHAR(64)  NOT NULL,
  placed_at     DATETIME2     NOT NULL,
  status        NVARCHAR(32)  NOT NULL
);
