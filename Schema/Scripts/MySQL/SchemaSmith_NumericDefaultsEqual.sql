-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_NumericDefaultsEqual//

CREATE FUNCTION SchemaSmith_NumericDefaultsEqual(
    p_Live TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Declared TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_DataType VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
DETERMINISTIC
BEGIN
    -- MySQL stores a DECIMAL default at the column's scale: DEFAULT 0 on DECIMAL(12,2) comes back as
    -- '0.00'. Compared as text that never equals the declared '0', so the column was re-ALTERed on every
    -- deploy -- an idempotency break that exits 0 and logs success, so nothing surfaces it.
    --
    -- Scoped to decimal/numeric ON PURPOSE. Normalizing numerically for every type would make DEFAULT '0'
    -- and DEFAULT '0.00' on a VARCHAR column compare equal, and on a string column those are genuinely
    -- different defaults.
    IF p_Live IS NULL OR p_Declared IS NULL THEN
        RETURN 0;
    END IF;
    IF LOWER(p_DataType) NOT IN ('decimal', 'numeric') THEN
        RETURN 0;
    END IF;
    -- Only plain numeric literals. An expression default (or anything quoted) is left to the text
    -- comparison, which is the only thing that can judge it.
    IF TRIM(p_Live) NOT REGEXP '^-?[0-9]+(\.[0-9]+)?$'
       OR TRIM(p_Declared) NOT REGEXP '^-?[0-9]+(\.[0-9]+)?$' THEN
        RETURN 0;
    END IF;

    RETURN IF(CAST(TRIM(p_Live) AS DECIMAL(65,20)) = CAST(TRIM(p_Declared) AS DECIMAL(65,20)), 1, 0);
END //

DELIMITER ;
