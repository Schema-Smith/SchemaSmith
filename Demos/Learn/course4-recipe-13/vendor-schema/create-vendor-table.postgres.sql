-- Stands in for a vendor product creating its own table outside the package.
-- Guarded so re-running Step 1 (or resetting the lab) is safe.
CREATE TABLE IF NOT EXISTS public.vendor_order (
  order_id      INT          NOT NULL PRIMARY KEY,
  customer_ref  VARCHAR(64)  NOT NULL,
  placed_at     TIMESTAMP    NOT NULL,
  status        VARCHAR(32)  NOT NULL
);
