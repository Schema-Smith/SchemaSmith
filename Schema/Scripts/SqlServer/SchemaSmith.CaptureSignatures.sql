-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[CaptureSignatures]', 'P') IS NOT NULL DROP PROCEDURE [SchemaSmith].[CaptureSignatures]
GO
-- ALTER strips a module's signatures and counter-signatures (sys.crypt_properties), and the private key that made
-- them is not readable, so a signature cannot be scripted. What can be done is to sign again after the change, when
-- the certificate's or asymmetric key's private key is encrypted by the database master key, which the server can
-- use without a password. @Script is the ADD SIGNATURE statement for each of those; @Unsignable names the keys
-- whose private key is absent or password-protected, so the caller can say the module needs signing by hand.
CREATE PROCEDURE [SchemaSmith].[CaptureSignatures]
  @Schema NVARCHAR(128),
  @Name NVARCHAR(128),
  @Script NVARCHAR(MAX) OUTPUT,
  @Unsignable NVARCHAR(MAX) OUTPUT
AS
BEGIN
  SET NOCOUNT ON
  SET @Script = NULL
  SET @Unsignable = NULL
  DECLARE @v_ObjectId INT = OBJECT_ID(QUOTENAME(@Schema) + '.' + QUOTENAME(@Name))
  IF @v_ObjectId IS NULL RETURN
  DECLARE @v_Qualified NVARCHAR(300) = QUOTENAME(@Schema) + '.' + QUOTENAME(@Name)
  DECLARE @v_CrLf NVARCHAR(2) = CHAR(13) + CHAR(10)

  ;WITH Keys AS (
    SELECT cp.crypt_type, k.[name], k.pvt_key_encryption_type, k.kind
      FROM sys.crypt_properties cp
      CROSS APPLY (SELECT c.[name], c.pvt_key_encryption_type, 'CERTIFICATE' AS kind
                     FROM sys.certificates c WHERE c.thumbprint = cp.thumbprint
                   UNION ALL
                   SELECT a.[name], a.pvt_key_encryption_type, 'ASYMMETRIC KEY'
                     FROM sys.asymmetric_keys a WHERE a.thumbprint = cp.thumbprint) k
     WHERE cp.class = 1 AND cp.major_id = @v_ObjectId)
  SELECT @Script = (SELECT 'ADD ' + CASE WHEN k.crypt_type IN ('CPVC', 'CPVA') THEN 'COUNTER ' ELSE '' END + 'SIGNATURE TO OBJECT::' + @v_Qualified
                         + ' BY ' + k.kind + ' ' + QUOTENAME(k.[name]) + ';' + @v_CrLf
                      FROM Keys k
                     WHERE k.pvt_key_encryption_type = 'MK'
                     ORDER BY k.kind, k.[name]
                       FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'),
         @Unsignable = STUFF((SELECT ', ' + k.kind + ' ' + QUOTENAME(k.[name])
                                FROM Keys k
                               WHERE k.pvt_key_encryption_type <> 'MK'
                               ORDER BY k.kind, k.[name]
                                 FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
END
