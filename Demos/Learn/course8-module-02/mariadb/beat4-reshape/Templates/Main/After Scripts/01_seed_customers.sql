-- Seed the diag_blackbox baseline with realistic customer rows — including two
-- that share an email (CustomerId 1 and 7). Harmless under the non-unique
-- IX_Customer_Email; it becomes the dirty data Module 1's UNIQUE flip trips on.
-- Run-once (no [ALWAYS]): tracked in CompletedMigrationScripts, so it seeds on the
-- baseline deploy and never re-runs — which is what lets the manual dedupe stick.
INSERT INTO `Customer` (`CustomerId`, `Email`, `FullName`)
SELECT * FROM (SELECT 1 AS c, 'ana.f@shop.test' AS e, 'Ana Fielding' AS f
  UNION ALL SELECT 2, 'ben.c@shop.test',   'Ben Cortez'
  UNION ALL SELECT 3, 'carla.d@shop.test', 'Carla Dunn'
  UNION ALL SELECT 7, 'ana.f@shop.test',   'Ana Fielding-Reyes') AS v
WHERE NOT EXISTS (SELECT 1 FROM `Customer`);
