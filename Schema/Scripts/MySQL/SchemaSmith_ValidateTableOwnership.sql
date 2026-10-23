-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

-- Refuses a run that declares a table another product owns. Called before MissingTableAndColumnQuench, which renames
-- and adds columns (and drops a functional index ahead of a rename), so a refusal leaves the owner's table as it was.
DROP PROCEDURE IF EXISTS SchemaSmith_ValidateTableOwnership//

CREATE PROCEDURE SchemaSmith_ValidateTableOwnership(
    IN p_ProductName VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
)
SQL SECURITY DEFINER
BEGIN
    DECLARE v_ConflictingTable VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_ConflictingOwner VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

    -- SchemaSmith_IdentifierKey rather than CONVERT(... USING utf8mb4) on the identifier halves:
    -- CONVERT() yields the charset's DEFAULT collation, which is case-INSENSITIVE, so this check used
    -- to treat `CaseProbe` and `caseprobe` as one table. On a server with lower_case_table_names = 0
    -- those are two different tables, and a second product declaring the lowercase twin was refused
    -- with "Table CaseProbe is already owned by another product" -- naming a table it had not
    -- declared. IdentifierKey asks the server which it is and folds both sides only when the server
    -- folds identifiers itself, so behaviour on lower_case_table_names >= 1 is unchanged.
    SELECT po.ObjectName, po.ProductName
    INTO v_ConflictingTable, v_ConflictingOwner
    FROM _SchemaSmith_Tables t
    INNER JOIN SchemaSmith_ProductOwnership po
        ON (SchemaSmith_IdentifierKey(po.ObjectName) = SchemaSmith_IdentifierKey(SchemaSmith_StripBacktickWrapping(t.TableName))
            -- A table declared under a new name is still the old one until the rename, which runs only when the old name
            -- exists and the new one does not.
            OR (COALESCE(t.OldName, '') <> ''
                AND SchemaSmith_IdentifierKey(po.ObjectName) = SchemaSmith_IdentifierKey(SchemaSmith_StripBacktickWrapping(t.OldName))
                AND NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES it
                                 WHERE SchemaSmith_IdentifierKey(it.TABLE_SCHEMA) = SchemaSmith_IdentifierKey(p_DatabaseName)
                                   AND SchemaSmith_IdentifierKey(it.TABLE_NAME) = SchemaSmith_IdentifierKey(SchemaSmith_StripBacktickWrapping(t.TableName)))))
        AND SchemaSmith_IdentifierKey(po.ObjectSchema) = SchemaSmith_IdentifierKey(p_DatabaseName)
        AND po.ObjectType = 'TABLE'
    WHERE CONVERT(po.ProductName USING utf8mb4) != CONVERT(p_ProductName USING utf8mb4)
    LIMIT 1;

    IF v_ConflictingTable IS NOT NULL THEN
        -- Use user variable for dynamic message (SIGNAL requires constant or user variable)
        SET @schemasmith_error_msg = CONCAT('Table ', v_ConflictingTable, ' is already owned by another product: ', v_ConflictingOwner);
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = @schemasmith_error_msg;
    END IF;
END//

DELIMITER ;
