-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_SetSystemVersioningAlterHistory//

CREATE PROCEDURE SchemaSmith_SetSystemVersioningAlterHistory(
    IN p_Mode VARCHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
)
SQL SECURITY DEFINER
BEGIN
    -- MariaDb variant override of the shared MySQL no-op. See the MySQL base definition for why the
    -- divergence lives in a separate file rather than a version-gated branch.
    --
    -- MariaDB refuses every column DDL on a system-versioned table by default:
    --   ERROR 4119: Not allowed for system-versioned `db`.`t`.
    --               Change @@system_versioning_alter_history to proceed with ALTER.
    --
    -- KEEP does not merely permit the change -- it applies the DDL to the HISTORICAL rows as well, so
    -- the stored history is rewritten to a shape it never actually had. That is a data-retention
    -- decision rather than a syntax one, which is why it is an authored setting that defaults to off
    -- instead of something the deploy engine assumes on the operator's behalf.
    --
    -- Anything other than KEEP leaves the engine default (ERROR) in place. That is deliberate: the
    -- engine then refuses precisely when a change genuinely requires rewriting history, and never on an
    -- idempotent re-deploy where nothing is changing. A pre-emptive refusal here would have to know that
    -- column work is pending, which only the column passes themselves know -- duplicating their
    -- predicates would refuse healthy re-deploys and pin this guard to their shape.
    --
    -- SESSION scope, never GLOBAL: the permission must not outlive this connection and leak to whatever
    -- runs next on a pooled one.
    --
    -- The variable arrived with system versioning in 10.3 and the supported floor is 10.2. Unlike a
    -- missing column, an unknown system variable is an error wherever the statement is REACHED, so the
    -- version test guards the assignment rather than relying on deferred resolution.
    -- The version test alone is NOT enough, and that is the whole reason for the dynamic SQL below.
    -- MariaDB resolves a system variable at CREATE PROCEDURE time, exactly as MySQL does (see the
    -- MySQL base definition), so a bare mention inside this branch fails to CREATE on 10.2 -- the
    -- supported floor -- with ERROR 1193, taking the whole kindle down and every test with it. Verified
    -- live on 10.2. Naming the variable only inside a string literal defers resolution to EXECUTE,
    -- which the version guard then keeps unreachable below 10.3. Confirmed still effective on 11.4:
    -- the session value moves ERROR -> KEEP.
    IF UPPER(COALESCE(p_Mode, '')) = 'KEEP' AND SchemaSmith_ServerVersionNum() >= 1003 THEN
        SET @ss_avh_sql = 'SET SESSION system_versioning_alter_history = 1';  -- 1 = KEEP; the enum's other value is ERROR
        PREPARE ss_avh_stmt FROM @ss_avh_sql;
        EXECUTE ss_avh_stmt;
        DEALLOCATE PREPARE ss_avh_stmt;
    END IF;
END //

DELIMITER ;
