DROP TRIGGER IF EXISTS `Production_WorkOrder_iWorkOrder`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE TRIGGER `Production_WorkOrder_iWorkOrder` AFTER INSERT ON `Production_WorkOrder` FOR EACH ROW BEGIN
BEGIN
BEGIN
    INSERT INTO `Production_TransactionHistory`(
        `ProductID`
        ,`ReferenceOrderID`
        ,`TransactionType`
        ,`TransactionDate`
        ,`Quantity`
        ,`ActualCost`)
    VALUES (
        NEW.`ProductID`
        ,NEW.`WorkOrderID`
        ,'W'
        ,NOW()
        ,NEW.`OrderQty`
        ,0
    );
END;
END;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
