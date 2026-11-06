SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER TRIGGER [dbo].[trg_Orders_Legacy] ON [dbo].[Orders] AFTER INSERT AS

INSERT dbo.OrderAudit (OrderId, Note) SELECT OrderId, N'legacy' FROM inserted;

GO

DISABLE TRIGGER [dbo].[trg_Orders_Legacy] ON [dbo].[Orders]
GO
