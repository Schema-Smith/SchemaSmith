DROP TRIGGER IF EXISTS `Purchasing_Vendor_dVendor`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE TRIGGER `Purchasing_Vendor_dVendor` BEFORE DELETE ON `Purchasing_Vendor` FOR EACH ROW BEGIN
BEGIN
BEGIN
    SIGNAL SQLSTATE '45000'
        SET MESSAGE_TEXT = 'Vendors cannot be deleted. They can only be marked as not active.';
END;
END;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
