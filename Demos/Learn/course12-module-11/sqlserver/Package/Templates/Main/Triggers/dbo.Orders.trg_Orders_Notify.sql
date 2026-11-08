SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER TRIGGER [dbo].[trg_Orders_Notify] ON [dbo].[Orders] AFTER INSERT AS

INSERT dbo.OrderAudit (OrderId, Note) SELECT OrderId, N'notified' FROM inserted;

GO
