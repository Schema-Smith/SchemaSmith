DROP TRIGGER IF EXISTS `Production_WorkOrder_uWorkOrder`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE TRIGGER `Production_WorkOrder_uWorkOrder` AFTER UPDATE ON `Production_WorkOrder` FOR EACH ROW BEGIN
BEGIN
BEGIN
    IF (NEW.`ProductID` <> OLD.`ProductID`) OR (NEW.`OrderQty` <> OLD.`OrderQty`) THEN
        INSERT INTO `Production_TransactionHistory`(
            `ProductID`
            ,`ReferenceOrderID`
            ,`TransactionType`
            ,`TransactionDate`
            ,`Quantity`)
        VALUES (
            NEW.`ProductID`
            ,NEW.`WorkOrderID`
            ,'W'
            ,NOW()
            ,NEW.`OrderQty`
        );
    END IF;
END;
END;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
