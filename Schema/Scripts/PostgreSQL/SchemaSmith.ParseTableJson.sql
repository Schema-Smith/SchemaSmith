-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The table model shredded into the session's working set, as a procedure taking the model as an
-- ARGUMENT. SchemaQuench used to do this in an anonymous DO block with the whole model escaped into it
-- as a string literal, which meant a large product sent tens of megabytes of JSON as statement text on
-- every work unit -- parsed as SQL before a single row was shredded. A DO block cannot take parameters,
-- so the body had to become a callable object before the payload could be sent as data.
--
-- MySQL already had this shape (SchemaSmith_ParseTableJson); PostgreSQL was the last engine inlining
-- the model. The temp tables the body creates are session-scoped, not procedure-scoped, so every later
-- step in the same session reads them exactly as it did when a DO block created them.
--
-- SchemaSmith.TableQuench inlines the same parse source below, so the two paths shred identically by
-- construction rather than by review. (Do not name the substitution token in this comment -- the kindler
-- replaces every occurrence in the file, so a token mentioned in prose gets the whole parse body pasted
-- into the middle of a comment line, and everything after it escapes the procedure.)

CREATE OR REPLACE PROCEDURE "SchemaSmith"."ParseTableJson"
  (p_TableDefinitions TEXT,
   p_UpdateFillFactor BOOLEAN = FALSE)
  LANGUAGE plpgsql
AS $$
DECLARE
  table_json JSON = p_TableDefinitions::JSON;
  sql_script TEXT = '';
BEGIN
{{ParseJson}}
END $$;
