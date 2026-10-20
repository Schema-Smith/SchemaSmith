-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP PROCEDURE IF EXISTS `SchemaSmith_ExecuteOnline`;

DELIMITER //

-- OnlineApply on the MySQL family: runs one ALTER TABLE (or CREATE INDEX) in the lowest-lock form the engine accepts.
-- OFF runs it as written. PREFER and REQUIRE ask for ALGORITHM=INSTANT (p_TryInstant; MySQL 8.0.12+, MariaDB 10.3+),
-- then ALGORITHM=INPLACE, LOCK=NONE, and the engine decides: it refuses a form it cannot honour (1845, 1846, and on
-- MySQL 4092 once the table's instant row versions are spent), and the refusal moves to the next form. PREFER ends
-- with the statement as written; REQUIRE refuses, naming the statement. p_Form says what ran: 'instant', 'online',
-- 'unavailable', or NULL under OFF. p_CreateIndexForm marks a CREATE INDEX, whose options take no commas.
-- The parameters name their character set: undeclared, they take the database's, and a latin1 database cannot hold a
-- statement that names a table or column in another script.
CREATE PROCEDURE `SchemaSmith_ExecuteOnline`(IN p_Sql LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
                                             IN p_Mode VARCHAR(8) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
                                             IN p_TryInstant TINYINT, IN p_CreateIndexForm TINYINT,
                                             IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
                                             IN p_TableName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
                                             OUT p_Form VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
BEGIN
    DECLARE v_maria TINYINT DEFAULT IF(VERSION() LIKE '%MariaDB%', 1, 0);
    DECLARE v_sep VARCHAR(2) DEFAULT IF(p_CreateIndexForm = 1, ' ', ', ');
    DECLARE v_errno INT DEFAULT 0;
    DECLARE v_reason TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    DECLARE v_cap INT DEFAULT NULL;
    SET p_Form = NULL;

    IF COALESCE(p_Mode, 'OFF') = 'OFF' THEN
        SET @ss_online_sql = p_Sql;
        PREPARE ss_online_stmt FROM @ss_online_sql;
        EXECUTE ss_online_stmt;
        DEALLOCATE PREPARE ss_online_stmt;
    ELSE
        IF p_TryInstant = 1 AND p_CreateIndexForm = 0
           AND ((v_maria = 1 AND SchemaSmith_ServerVersionNum() >= 1003) OR (v_maria = 0 AND SchemaSmith_ServerVersionPatchNum() >= 80012)) THEN
            instant_form: BEGIN
                DECLARE EXIT HANDLER FOR SQLEXCEPTION
                BEGIN
                    GET DIAGNOSTICS CONDITION 1 v_errno = MYSQL_ERRNO, v_reason = MESSAGE_TEXT;
                    IF NOT (v_errno IN (1845, 1846) OR (v_errno = 4092 AND v_maria = 0)) THEN
                        RESIGNAL;
                    END IF;
                END;
                SET @ss_online_sql = CONCAT(p_Sql, ', ALGORITHM=INSTANT');
                PREPARE ss_online_stmt FROM @ss_online_sql;
                EXECUTE ss_online_stmt;
                DEALLOCATE PREPARE ss_online_stmt;
                SET p_Form = 'instant';
            END instant_form;

            -- MY-#7: each instant add or drop spends one of the table's row versions (64 to MySQL 9.0, 255 from 9.1),
            -- and at the cap the next change has to rebuild. Read through PREPARE: the column is 8.0.29+, and a
            -- procedure that names it does not compile below that.
            IF v_maria = 0 AND SchemaSmith_ServerVersionPatchNum() >= 80029 THEN
                SET v_cap = IF(SchemaSmith_ServerVersionNum() >= 901, 255, 64);
                IF v_errno = 4092 THEN
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                    VALUES (CONNECTION_ID(), CONCAT('  WARNING: ', p_TableName, ' has used all ', v_cap,
                                                    ' of its instant row versions; this change rebuilds it, which resets the count'));
                ELSEIF p_Form = 'instant' THEN
                    SET @ss_row_versions = NULL;
                    SET @ss_online_sql = CONCAT('SELECT MAX(TOTAL_ROW_VERSIONS) INTO @ss_row_versions FROM INFORMATION_SCHEMA.INNODB_TABLES WHERE LOWER(NAME) = LOWER(''',
                                                REPLACE(CONCAT(p_DatabaseName, '/', p_TableName), '''', ''''''), ''')');
                    PREPARE ss_online_stmt FROM @ss_online_sql;
                    EXECUTE ss_online_stmt;
                    DEALLOCATE PREPARE ss_online_stmt;
                    IF @ss_row_versions >= v_cap - 8 THEN
                        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                        VALUES (CONNECTION_ID(), CONCAT('  WARNING: ', p_TableName, ' has used ', @ss_row_versions, ' of its ', v_cap,
                                                        ' instant row versions; at ', v_cap, ' a column change rebuilds it, and a rebuild resets the count'));
                    END IF;
                END IF;
            END IF;
        END IF;

        IF p_Form IS NULL THEN
            online_form: BEGIN
                DECLARE EXIT HANDLER FOR SQLEXCEPTION
                BEGIN
                    GET DIAGNOSTICS CONDITION 1 v_errno = MYSQL_ERRNO, v_reason = MESSAGE_TEXT;
                    IF v_errno NOT IN (1845, 1846) THEN
                        RESIGNAL;
                    END IF;
                END;
                SET @ss_online_sql = CONCAT(p_Sql, v_sep, 'ALGORITHM=INPLACE', v_sep, 'LOCK=NONE');
                PREPARE ss_online_stmt FROM @ss_online_sql;
                EXECUTE ss_online_stmt;
                DEALLOCATE PREPARE ss_online_stmt;
                SET p_Form = 'online';
            END online_form;
        END IF;

        IF p_Form IS NULL THEN
            IF p_Mode = 'REQUIRE' THEN
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                VALUES (CONNECTION_ID(), CONCAT('  OnlineApply is Require, and this cannot run online: ', p_Sql, ' (', v_reason, ')'));
                SET @ss_online_msg = LEFT(CONCAT('OnlineApply is Require, and ', p_TableName, ' cannot change online: ', v_reason), 128);
                SIGNAL SQLSTATE 'HY000' SET MESSAGE_TEXT = @ss_online_msg;
            END IF;
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            VALUES (CONNECTION_ID(), CONCAT('    (online unavailable: ', v_reason, ')'));
            SET @ss_online_sql = p_Sql;
            PREPARE ss_online_stmt FROM @ss_online_sql;
            EXECUTE ss_online_stmt;
            DEALLOCATE PREPARE ss_online_stmt;
            SET p_Form = 'unavailable';
        ELSEIF p_Form = 'instant' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), '    (instant: ALGORITHM=INSTANT)');
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), '    (online: ALGORITHM=INPLACE, LOCK=NONE)');
        END IF;
    END IF;
END //

DELIMITER ;
