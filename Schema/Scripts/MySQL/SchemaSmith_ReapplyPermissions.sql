-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_ReapplyPermissions//

-- Runs what SchemaSmith_CapturePermissions rendered, once the routine exists again, and records it in the change
-- audit as 'reapplied' -- or, under WhatIf, lists it and records 'wouldReapply'. Nothing happens for a NULL script.
-- The script is one GRANT per line; PREPARE runs one statement, and a grant to a second grantee is a second
-- statement in the engine's own grammar, so the loop is the fold.
CREATE PROCEDURE SchemaSmith_ReapplyPermissions(
    IN p_Script TEXT,
    IN p_ObjectType VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_ObjectName VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT
)
MODIFIES SQL DATA
reapply: BEGIN
    DECLARE v_Rest TEXT;
    DECLARE v_Break INT;

    IF p_Script IS NULL OR p_Script = '' THEN
        LEAVE reapply;
    END IF;

    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Would re-apply the grants on ', p_ObjectName));
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), p_Script);
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        VALUES (CONNECTION_ID(), p_ObjectType, p_ObjectName, 'wouldReapply');
        LEAVE reapply;
    END IF;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
    VALUES (CONNECTION_ID(), CONCAT('  Re-applying the grants on ', p_ObjectName));
    SET v_Rest = p_Script;
    WHILE v_Rest IS NOT NULL AND v_Rest <> '' DO
        SET v_Break = LOCATE('\n', v_Rest);
        IF v_Break = 0 THEN
            SET @ss_reapply_sql = v_Rest;
            SET v_Rest = '';
        ELSE
            SET @ss_reapply_sql = LEFT(v_Rest, v_Break - 1);
            SET v_Rest = SUBSTRING(v_Rest, v_Break + 1);
        END IF;
        IF TRIM(@ss_reapply_sql) <> '' THEN
            PREPARE ss_reapply FROM @ss_reapply_sql;
            EXECUTE ss_reapply;
            DEALLOCATE PREPARE ss_reapply;
        END IF;
    END WHILE;
    INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    VALUES (CONNECTION_ID(), p_ObjectType, p_ObjectName, 'reapplied');
END//

DELIMITER ;
