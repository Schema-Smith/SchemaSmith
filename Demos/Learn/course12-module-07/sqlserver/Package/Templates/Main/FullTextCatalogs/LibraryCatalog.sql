IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'LibraryCatalog')
  CREATE FULLTEXT CATALOG [LibraryCatalog];
