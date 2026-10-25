-- Price history is append-only: a DELETE does nothing.
CREATE OR REPLACE RULE price_history_append_only AS
  ON DELETE TO public.price_history DO INSTEAD NOTHING;
