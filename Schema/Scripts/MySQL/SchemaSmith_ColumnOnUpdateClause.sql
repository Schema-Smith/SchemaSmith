-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_ColumnOnUpdateClause//

CREATE FUNCTION SchemaSmith_ColumnOnUpdateClause(
    p_Extra TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS VARCHAR(30) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    DECLARE v_Pos INT;
    DECLARE v_Rest VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_End INT;

    IF p_Extra IS NULL THEN
        RETURN NULL;
    END IF;

    -- INFORMATION_SCHEMA.COLUMNS.EXTRA can carry several flags at once (auto_increment, VIRTUAL
    -- GENERATED, INVISIBLE, DEFAULT_GENERATED) in combinations like 'DEFAULT_GENERATED on update
    -- CURRENT_TIMESTAMP', so this isolates just the on-update token rather than assuming EXTRA equals
    -- one value. No REGEXP_SUBSTR/REPLACE: those are MySQL 8.0.4+ only and this function is called
    -- unconditionally on every version -- unlike SchemaSmith_SupportsInvisibleColumn's floor, ON UPDATE
    -- CURRENT_TIMESTAMP predates both engines' hard floors (MySQL 5.6.5; MariaDB inherited it), so
    -- there is no version to gate this behind and no unreached branch to hide a newer-only builtin in.
    SET v_Pos = LOCATE('on update', LOWER(p_Extra));
    IF v_Pos = 0 THEN
        RETURN NULL;
    END IF;

    -- Skip 'on update' (9 chars) plus the separating space, then cut at the next space (or end of
    -- string) so a flag that happens to follow (e.g. a trailing ' INVISIBLE') isn't swept in.
    SET v_Rest = SUBSTRING(p_Extra, v_Pos + 10);
    SET v_End = LOCATE(' ', v_Rest);
    IF v_End > 0 THEN
        SET v_Rest = LEFT(v_Rest, v_End - 1);
    END IF;

    -- MariaDB reports this the same divergent way it reports COLUMN_DEFAULT (lower-case, function-call
    -- parens: current_timestamp() / current_timestamp(3)) -- SchemaSmith_NormalizeColumnDefault already
    -- folds that exact shape to MySQL's canonical CURRENT_TIMESTAMP / CURRENT_TIMESTAMP(n) form (identity
    -- on MySQL, fold on MariaDB via its Scripts/MariaDb override), so reuse it instead of duplicating the
    -- case/paren-stripping logic here.
    RETURN SchemaSmith_NormalizeColumnDefault(v_Rest);
END //

DELIMITER ;
