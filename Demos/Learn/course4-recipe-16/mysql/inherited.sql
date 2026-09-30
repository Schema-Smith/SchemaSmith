-- The database you inherited: built by hand, never by a package. The indexes, foreign keys and
-- checks are created deliberately out of name order.
CREATE TABLE customer (customer_id INT NOT NULL PRIMARY KEY);
CREATE TABLE order_line (
  order_line_id   INT NOT NULL PRIMARY KEY,
  customer_id     INT NOT NULL,
  alt_customer_id INT NULL,
  qty             INT NOT NULL,
  price           DECIMAL(10,2) NOT NULL,
  KEY ix_zeta (qty),
  KEY ix_alpha (price),
  KEY ix_mid (customer_id),
  CONSTRAINT fk_zeta FOREIGN KEY (customer_id) REFERENCES customer (customer_id),
  CONSTRAINT fk_alpha FOREIGN KEY (alt_customer_id) REFERENCES customer (customer_id),
  CONSTRAINT ck_zeta CHECK (qty > 0),
  CONSTRAINT ck_alpha CHECK (price >= 0)
);
