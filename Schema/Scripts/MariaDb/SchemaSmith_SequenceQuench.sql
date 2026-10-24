-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_SequenceQuench//

-- Converges the declared sequences (MariaDB 10.3+). Every attribute is alterable in place, and THE CURRENT VALUE IS
-- NEVER TOUCHED: a sequence's position records which numbers have been handed out, so it is data. Start applies at
-- creation and is converged as the recorded start value, never as a RESTART. An attribute the declaration leaves
-- out is left to the engine at creation and not compared afterwards. Below 10.3 there are no sequences, so a
-- declared one is skipped as a downgrade; CREATE SEQUENCE ... AS <type> is 11.5+, so another type is a downgrade
-- below it. The MySQL twin of this procedure only refuses: MySQL has no sequence at any version.
CREATE PROCEDURE SchemaSmith_SequenceQuench(
    IN p_DatabaseName VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_Sequences LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT
)
MODIFIES SQL DATA
BEGIN
    DECLARE v_count INT DEFAULT 0;
    DECLARE v_idx INT DEFAULT 0;
    DECLARE v_Version INT DEFAULT SchemaSmith_ServerVersionNum();
    DECLARE v_Name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_DataType VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Apply TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Start BIGINT;
    DECLARE v_Increment BIGINT;
    DECLARE v_MinValue BIGINT;
    DECLARE v_MaxValue BIGINT;
    DECLARE v_Cache BIGINT;
    DECLARE v_Cycle TINYINT;
    DECLARE v_Exists INT;
    DECLARE v_Qualified VARCHAR(140) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_TypeClause VARCHAR(40) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Clauses TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Sql LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

    SET v_count = COALESCE(JSON_LENGTH(p_Sequences), 0);
    WHILE v_idx < v_count DO
        SET v_Name = TRIM(BOTH '`' FROM SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].Name'))));
        SET v_DataType = LOWER(COALESCE(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].DataType')))), ''), 'bigint'));
        -- The engine stores integer as int, so the declared spelling is compared, and created, as int.
        IF v_DataType LIKE 'integer%' THEN SET v_DataType = CONCAT('int', SUBSTRING(v_DataType, 8)); END IF;
        -- A JSON number comes back as its text; a missing or null property as NULL.
        SET @ss_seq_raw = NULLIF(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].Start')), 'null');
        SET v_Start = IF(@ss_seq_raw IS NULL, NULL, CAST(@ss_seq_raw AS SIGNED));
        SET @ss_seq_raw = NULLIF(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].Increment')), 'null');
        SET v_Increment = IF(@ss_seq_raw IS NULL, 1, CAST(@ss_seq_raw AS SIGNED));
        SET @ss_seq_raw = NULLIF(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].MinValue')), 'null');
        SET v_MinValue = IF(@ss_seq_raw IS NULL, NULL, CAST(@ss_seq_raw AS SIGNED));
        SET @ss_seq_raw = NULLIF(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].MaxValue')), 'null');
        SET v_MaxValue = IF(@ss_seq_raw IS NULL, NULL, CAST(@ss_seq_raw AS SIGNED));
        SET @ss_seq_raw = NULLIF(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].Cache')), 'null');
        SET v_Cache = IF(@ss_seq_raw IS NULL, 1000, CAST(@ss_seq_raw AS SIGNED));
        SET v_Cycle = IF(LOWER(TRIM(COALESCE(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].Cycle')), 'false'))) = 'true', 1, 0);
        SET v_Apply = SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_Sequences, CONCAT('$[', v_idx, '].ShouldApplyExpression')));
        SET v_idx = v_idx + 1;

        SET @ss_seq_apply = 1;
        IF v_Apply IS NOT NULL AND TRIM(v_Apply) <> '' THEN
            SET @ss_seq_sql = CONCAT('SELECT CASE WHEN (', v_Apply, ') THEN 1 ELSE 0 END INTO @ss_seq_apply');
            PREPARE ss_seq FROM @ss_seq_sql;
            EXECUTE ss_seq;
            DEALLOCATE PREPARE ss_seq;
        END IF;

        IF @ss_seq_apply = 1 THEN
            IF v_Version < 1003 THEN
                IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
                    SET @ss_msg = LEFT(CONCAT('Sequence ', v_Name, ' requires MariaDB 10.3 (detected ', VERSION(), ').'), 128);
                    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
                END IF;
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                VALUES (CONNECTION_ID(), CONCAT('  Skipping sequence ', v_Name, ': sequences require MariaDB 10.3 (detected ', VERSION(), ')'));
                INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                VALUES (CONNECTION_ID(), 'sequence (MariaDB 10.3)', v_Name, 'downgraded');
            ELSE
                SET v_Qualified = CONCAT('`', REPLACE(p_DatabaseName, '`', '``'), '`.`', REPLACE(v_Name, '`', '``'), '`');
                SET v_TypeClause = '';
                IF v_DataType <> 'bigint' THEN
                    IF v_Version >= 1105 THEN
                        SET v_TypeClause = CONCAT(' AS ', UPPER(v_DataType));
                    ELSE
                        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
                            SET @ss_msg = LEFT(CONCAT('Sequence ', v_Name, ' declares type ', v_DataType, ', which requires MariaDB 11.5 (detected ', VERSION(), ').'), 128);
                            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
                        END IF;
                        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                        VALUES (CONNECTION_ID(), CONCAT('  Sequence ', v_Name, ' declares type ', v_DataType, ', which requires MariaDB 11.5 (detected ', VERSION(), '); it is a signed bigint here'));
                        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                        VALUES (CONNECTION_ID(), 'sequence type (MariaDB 11.5)', v_Name, 'downgraded');
                    END IF;
                END IF;

                SELECT COUNT(*) INTO v_Exists
                  FROM INFORMATION_SCHEMA.TABLES
                 WHERE CONVERT(TABLE_SCHEMA USING utf8mb4) COLLATE utf8mb4_general_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_general_ci
                   AND CONVERT(TABLE_NAME USING utf8mb4) COLLATE utf8mb4_general_ci = CONVERT(v_Name USING utf8mb4) COLLATE utf8mb4_general_ci
                   AND TABLE_TYPE = 'SEQUENCE';

                IF v_Exists = 0 THEN
                    SET v_Sql = CONCAT('CREATE SEQUENCE ', v_Qualified, v_TypeClause,
                                       IF(v_Start IS NULL, '', CONCAT(' START WITH ', v_Start)),
                                       ' INCREMENT BY ', v_Increment,
                                       IF(v_MinValue IS NULL, '', CONCAT(' MINVALUE ', v_MinValue)),
                                       IF(v_MaxValue IS NULL, '', CONCAT(' MAXVALUE ', v_MaxValue)),
                                       ' CACHE ', v_Cache,
                                       IF(v_Cycle = 1, ' CYCLE', ' NOCYCLE'));
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Create sequence ', v_Name));
                    IF p_WhatIf = 1 THEN
                        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_Sql);
                        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                        VALUES (CONNECTION_ID(), 'sequence', v_Name, 'wouldCreate');
                    ELSE
                        SET @ss_seq_sql = v_Sql;
                        PREPARE ss_seq FROM @ss_seq_sql;
                        EXECUTE ss_seq;
                        DEALLOCATE PREPARE ss_seq;
                        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                        VALUES (CONNECTION_ID(), 'sequence', v_Name, 'created');
                    END IF;
                ELSE
                    -- The sequence is a one-row table of its own attributes; reading it never moves it.
                    SET @ss_seq_sql = CONCAT('SELECT minimum_value, maximum_value, start_value, increment, cache_size, cycle_option ',
                                             'INTO @ss_seq_min, @ss_seq_max, @ss_seq_start, @ss_seq_inc, @ss_seq_cache, @ss_seq_cycle FROM ', v_Qualified);
                    PREPARE ss_seq FROM @ss_seq_sql;
                    EXECUTE ss_seq;
                    DEALLOCATE PREPARE ss_seq;
                    SET @ss_seq_type = 'bigint';
                    IF v_Version >= 1105 THEN
                        -- INFORMATION_SCHEMA.SEQUENCES is 11.5+, so it is only ever named inside dynamic SQL.
                        SET @ss_seq_sql = CONCAT('SELECT LOWER(DATA_TYPE) INTO @ss_seq_type FROM INFORMATION_SCHEMA.SEQUENCES WHERE SEQUENCE_SCHEMA = ''',
                                                 REPLACE(p_DatabaseName, '''', ''''''), ''' AND SEQUENCE_NAME = ''', REPLACE(v_Name, '''', ''''''), '''');
                        PREPARE ss_seq FROM @ss_seq_sql;
                        EXECUTE ss_seq;
                        DEALLOCATE PREPARE ss_seq;
                    END IF;

                    SET v_Clauses = '';
                    -- A user variable takes the connection's collation; the declared type is compared under one collation on both sides.
                    IF v_TypeClause <> '' AND CONVERT(COALESCE(@ss_seq_type, 'bigint') USING utf8mb4) COLLATE utf8mb4_bin <> CONVERT(v_DataType USING utf8mb4) COLLATE utf8mb4_bin THEN
                        SET v_Clauses = CONCAT(v_Clauses, v_TypeClause);
                    END IF;
                    IF v_Start IS NOT NULL AND @ss_seq_start <> v_Start THEN SET v_Clauses = CONCAT(v_Clauses, ' START WITH ', v_Start); END IF;
                    IF @ss_seq_inc <> v_Increment THEN SET v_Clauses = CONCAT(v_Clauses, ' INCREMENT BY ', v_Increment); END IF;
                    IF v_MinValue IS NOT NULL AND @ss_seq_min <> v_MinValue THEN SET v_Clauses = CONCAT(v_Clauses, ' MINVALUE ', v_MinValue); END IF;
                    IF v_MaxValue IS NOT NULL AND @ss_seq_max <> v_MaxValue THEN SET v_Clauses = CONCAT(v_Clauses, ' MAXVALUE ', v_MaxValue); END IF;
                    IF @ss_seq_cache <> v_Cache THEN SET v_Clauses = CONCAT(v_Clauses, ' CACHE ', v_Cache); END IF;
                    IF @ss_seq_cycle <> v_Cycle THEN SET v_Clauses = CONCAT(v_Clauses, IF(v_Cycle = 1, ' CYCLE', ' NOCYCLE')); END IF;

                    IF v_Clauses <> '' THEN
                        SET v_Sql = CONCAT('ALTER SEQUENCE ', v_Qualified, v_Clauses);
                        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Alter sequence ', v_Name));
                        IF p_WhatIf = 1 THEN
                            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_Sql);
                            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                            VALUES (CONNECTION_ID(), 'sequence', v_Name, 'wouldModify');
                        ELSE
                            SET @ss_seq_sql = v_Sql;
                            PREPARE ss_seq FROM @ss_seq_sql;
                            EXECUTE ss_seq;
                            DEALLOCATE PREPARE ss_seq;
                            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                            VALUES (CONNECTION_ID(), 'sequence', v_Name, 'modified');
                        END IF;
                    END IF;
                END IF;
            END IF;
        END IF;
    END WHILE;
END//

DELIMITER ;
