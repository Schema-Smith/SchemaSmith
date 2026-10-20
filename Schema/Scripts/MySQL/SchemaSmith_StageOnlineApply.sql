-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP PROCEDURE IF EXISTS `SchemaSmith_StageOnlineApply`;

DELIMITER //

-- OnlineApply on the MySQL family. Fills _SchemaSmith_Tables.OnlineMode -- the table's own OnlineApply over the
-- deploy's (@ss_online_apply, which SchemaQuench sets before each pass), and OFF for a table created in this run, which
-- is empty -- in a statement of its own, since a function that read the table would hit 1137 inside every statement
-- that already does. Then, for a table not OFF, _SchemaSmith_OnlineColumnPositions holds where each column the deploy
-- adds goes: AFTER the declared column before it that is already there (or is added ahead of it), else FIRST, so it
-- lands in its declared place instead of last (MY-#8). Same column predicate as MissingTableAndColumnQuench's adds.
CREATE PROCEDURE `SchemaSmith_StageOnlineApply`()
BEGIN
    UPDATE _SchemaSmith_Tables t
       SET t.OnlineMode = CASE WHEN t.NewTable = 1 THEN 'OFF'
                               ELSE COALESCE(t.OnlineApply, UPPER(NULLIF(TRIM(@ss_online_apply), '')), 'OFF') END;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_OnlineColumnOrder;
    CREATE TEMPORARY TABLE _SchemaSmith_OnlineColumnOrder (
        RequiredPrimaryKey BIGINT AUTO_INCREMENT PRIMARY KEY,
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ColumnName VARCHAR(256) COLLATE utf8mb4_bin NOT NULL,
        Pos INT NOT NULL,
        Seq INT NOT NULL,
        Adding TINYINT NOT NULL,
        Present TINYINT NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_OnlineColumnOrder (TableName, ColumnName, Pos, Seq, Adding, Present)
    SELECT c.TableName, c.ColumnName, c.OrdinalPosition, c.RowId,
           IF(c.NewColumn = 1
              AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
              AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0), 1, 0),
           IF(c.NewColumn = 0
              OR ((c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
                  AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)), 1, 0)
      FROM _SchemaSmith_Columns c
      INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
     WHERE t.NewTable = 0
       AND t.OnlineMode <> 'OFF';

    -- A second copy: MySQL cannot open one temporary table twice in a statement (1137), and 5.7 has no window functions.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_OnlineColumnOrderPeer;
    CREATE TEMPORARY TABLE _SchemaSmith_OnlineColumnOrderPeer (
        RequiredPrimaryKey BIGINT AUTO_INCREMENT PRIMARY KEY,
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ColumnName VARCHAR(256) COLLATE utf8mb4_bin NOT NULL,
        Pos INT NOT NULL,
        Seq INT NOT NULL,
        Present TINYINT NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_OnlineColumnOrderPeer (TableName, ColumnName, Pos, Seq, Present)
    SELECT TableName, ColumnName, Pos, Seq, Present FROM _SchemaSmith_OnlineColumnOrder;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_OnlineColumnPositions;
    CREATE TEMPORARY TABLE _SchemaSmith_OnlineColumnPositions (
        RequiredPrimaryKey BIGINT AUTO_INCREMENT PRIMARY KEY,
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ColumnName VARCHAR(256) COLLATE utf8mb4_bin NOT NULL,
        Clause VARCHAR(300) NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_OnlineColumnPositions (TableName, ColumnName, Clause)
    SELECT a.TableName, a.ColumnName,
           COALESCE((SELECT CONCAT(' AFTER `', SchemaSmith_StripBacktickWrapping(b.ColumnName), '`')
                       FROM _SchemaSmith_OnlineColumnOrderPeer b
                      WHERE b.TableName = a.TableName
                        AND b.Present = 1
                        AND (b.Pos < a.Pos OR (b.Pos = a.Pos AND b.Seq < a.Seq))
                      ORDER BY b.Pos DESC, b.Seq DESC
                      LIMIT 1), ' FIRST')
      FROM _SchemaSmith_OnlineColumnOrder a
     WHERE a.Adding = 1;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_OnlineColumnOrder;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_OnlineColumnOrderPeer;
END //

DELIMITER ;
