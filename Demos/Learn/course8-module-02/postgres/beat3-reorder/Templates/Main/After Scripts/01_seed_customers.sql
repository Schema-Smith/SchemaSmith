-- Seed the diag_blackbox baseline with realistic customer rows — including two
-- that share an email (customerid 1 and 7). Harmless under the non-unique
-- ix_customer_email; it becomes the dirty data Module 1's UNIQUE flip trips on.
-- Run-once (no [ALWAYS]): tracked in CompletedMigrationScripts, so it seeds on the
-- baseline deploy and never re-runs — which is what lets the manual dedupe stick.
INSERT INTO public.customer (customerid, email, fullname)
SELECT * FROM (VALUES
  (1, 'ana.f@shop.test',   'Ana Fielding'),
  (2, 'ben.c@shop.test',   'Ben Cortez'),
  (3, 'carla.d@shop.test', 'Carla Dunn'),
  (7, 'ana.f@shop.test',   'Ana Fielding-Reyes')
) AS v(customerid, email, fullname)
WHERE NOT EXISTS (SELECT 1 FROM public.customer);
