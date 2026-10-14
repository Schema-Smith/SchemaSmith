DROP FUNCTION IF EXISTS `ufnGetSalesOrderStatusText`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE FUNCTION `ufnGetSalesOrderStatusText`(p_Status tinyint unsigned) RETURNS varchar(15) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
    DETERMINISTIC
BEGIN
    DECLARE ret VARCHAR(15);

    SET ret =
        CASE p_Status
            WHEN 1 THEN 'In process'
            WHEN 2 THEN 'Approved'
            WHEN 3 THEN 'Backordered'
            WHEN 4 THEN 'Rejected'
            WHEN 5 THEN 'Shipped'
            WHEN 6 THEN 'Cancelled'
            ELSE '** Invalid **'
        END;

    RETURN ret;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
