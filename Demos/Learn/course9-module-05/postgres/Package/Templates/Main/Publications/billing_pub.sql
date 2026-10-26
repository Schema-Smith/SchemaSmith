DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'billing_pub') THEN
    CREATE PUBLICATION billing_pub FOR TABLE public.invoice, public.audit_event;
  END IF;
END
$$;
