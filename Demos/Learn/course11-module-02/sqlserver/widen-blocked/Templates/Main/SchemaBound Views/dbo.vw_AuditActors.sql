CREATE OR ALTER VIEW dbo.vw_AuditActors WITH SCHEMABINDING AS
  SELECT AuditId, Actor FROM dbo.AuditTrail;
