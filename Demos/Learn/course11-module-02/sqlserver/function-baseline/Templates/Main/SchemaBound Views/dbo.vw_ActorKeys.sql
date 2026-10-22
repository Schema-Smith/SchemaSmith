CREATE OR ALTER VIEW dbo.vw_ActorKeys WITH SCHEMABINDING AS
  SELECT AuditId, dbo.fn_ActorKey(Actor) AS ActorKey FROM dbo.AuditTrail;
