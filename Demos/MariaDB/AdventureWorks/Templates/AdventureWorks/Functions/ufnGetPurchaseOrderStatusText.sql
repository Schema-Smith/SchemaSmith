DROP FUNCTION IF EXISTS `ufnGetPurchaseOrderStatusText`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE FUNCTION `ufnGetPurchaseOrderStatusText`(p_Status tinyint unsigned) RETURNS varchar(15) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
    DETERMINISTIC
BEGIN
    DECLARE ret VARCHAR(15);

    SET ret =
        CASE p_Status
            WHEN 1 THEN 'Pending'
            WHEN 2 THEN 'Approved'
            WHEN 3 THEN 'Rejected'
            WHEN 4 THEN 'Complete'
            ELSE '** Invalid **'
        END;

    RETURN ret;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
