SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER TRIGGER [dbo].[trg_Orders_Stamp] ON [dbo].[Orders] AFTER INSERT AS

INSERT dbo.OrderAudit (OrderId, Note) SELECT OrderId, N'stamped' FROM inserted;

GO

EXEC sp_settriggerorder @triggername = N'[dbo].[trg_Orders_Stamp]', @order = N'First', @stmttype = N'INSERT'
GO
