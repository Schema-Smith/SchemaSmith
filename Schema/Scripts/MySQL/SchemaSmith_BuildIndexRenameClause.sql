-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_BuildIndexRenameClause`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_BuildIndexRenameClause`(
    p_DatabaseName VARCHAR(128),
    p_TableName VARCHAR(128),
    p_OldIndexName VARCHAR(128),
    p_NewIndexName VARCHAR(128)
)
RETURNS TEXT CHARSET utf8mb4
READS SQL DATA
BEGIN
  -- One ALTER TABLE clause that renames an index. On MySQL 8.0 / MariaDB 10.5.2+ this is the
  -- metadata-only `RENAME INDEX old TO new`. Below that (MariaDB 10.2-10.5, which lack the syntax)
  -- it degrades to `DROP INDEX old, ADD [UNIQUE] INDEX new (cols)` -- a rebuild, but the only route
  -- pre-10.5.2. The recreated index is reconstructed from the OLD index's live catalog metadata
  -- (columns in SEQ_IN_INDEX order, prefix lengths, DESC key parts, uniqueness), so it matches the
  -- existing index exactly; only the name changes. (An index rename is detected only when the old
  -- index already matches the desired columns + uniqueness, so this is a true rename.)
  -- p_DatabaseName is no longer read here: the snapshot below was already scoped to it by the
  -- caller, so re-scoping here would be redundant. Kept in the signature to avoid touching all
  -- four call sites for a parameter that costs nothing to leave unused.
  DECLARE v_NonUnique TINYINT;
  DECLARE v_NormColumns TEXT;
  DECLARE v_ErrMsg VARCHAR(255);

  IF SchemaSmith_SupportsRenameIndex() = 1 THEN
    RETURN CONCAT('RENAME INDEX `', p_OldIndexName, '` TO `', p_NewIndexName, '`');
  END IF;

  -- The degrade path reads the ONE row the caller already built in _SchemaSmith_IdxDetectSnap
  -- (SchemaSmith_IndexOnlyQuench.sql / SchemaSmith_MissingIndexesAndConstraintsQuench.sql) rather
  -- than INFORMATION_SCHEMA.STATISTICS directly -- that snapshot's NormColumns already carries the
  -- SEQ_IN_INDEX-ordered column list with prefix lengths and DESC key parts, which is exactly the
  -- clause body an ADD INDEX needs. One row per (TableName, IndexName) by primary key, so this is a
  -- single reference to the temp table -- no repeat read, no ER_CANT_REOPEN_TABLE (1137) exposure.
  SELECT snap.NonUnique, snap.NormColumns
    INTO v_NonUnique, v_NormColumns
    FROM _SchemaSmith_IdxDetectSnap snap
   WHERE snap.TableKey = SchemaSmith_IdentifierKey(p_TableName)
     AND snap.IndexKey = SchemaSmith_NameKeyCI(p_OldIndexName);

  -- A missing snapshot row would otherwise concatenate NULL into a malformed ALTER TABLE
  -- (`ADD INDEX `x` ()` or worse); fail loudly instead of emitting broken DDL.
  IF v_NormColumns IS NULL THEN
      SET v_ErrMsg = CONCAT('SchemaSmith_BuildIndexRenameClause: missing detection-snapshot row for ', p_TableName, '.', p_OldIndexName);
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = v_ErrMsg;
  END IF;

  RETURN CONCAT('DROP INDEX `', p_OldIndexName, '`, ADD ',
      IF(v_NonUnique = 0, 'UNIQUE ', ''),
      'INDEX `', p_NewIndexName, '` (', v_NormColumns, ')');
END //

DELIMITER ;
