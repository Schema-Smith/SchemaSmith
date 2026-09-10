CREATE TABLE public.vendor_order (
  order_id      INT          NOT NULL PRIMARY KEY,
  customer_ref  VARCHAR(64)  NOT NULL,
  placed_at     TIMESTAMP    NOT NULL,
  status        VARCHAR(32)  NOT NULL
);
