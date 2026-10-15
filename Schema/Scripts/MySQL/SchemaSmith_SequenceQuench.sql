-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_SequenceQuench//

-- MySQL has no sequence at any version, so the only declared sequences this can receive come from a package that
-- was written for MariaDB. The MariaDb override of this file is the real procedure; this one refuses by name so a
-- package deployed to the wrong engine says so rather than silently skipping its sequences.
CREATE PROCEDURE SchemaSmith_SequenceQuench(
    IN p_DatabaseName VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_Sequences LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT
)
MODIFIES SQL DATA
BEGIN
    IF COALESCE(JSON_LENGTH(p_Sequences), 0) > 0 THEN
        SET @ss_msg = LEFT(CONCAT('Database ', p_DatabaseName, ' declares a sequence, and MySQL has no sequences: a Sequences/ folder belongs to a MariaDB package.'), 128);
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;
END//

DELIMITER ;
