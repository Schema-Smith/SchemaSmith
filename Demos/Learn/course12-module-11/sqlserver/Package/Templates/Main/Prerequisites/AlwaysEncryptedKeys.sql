-- SchemaSmith: generated, sha256=0a2e0a33d355117c06e6bf09b56461a1f40341a182a1a9c6eea45fda8acbe597
-- The Always Encrypted keys of declared_m11, written by SchemaTongs on every extraction: change the source database,
-- not this file. A key is created only where it is missing; one that exists is left as it is. KEY_PATH names a
-- certificate or vault key outside the database, which any client that reads the encrypted columns needs access to.
IF OBJECT_ID('sys.column_master_keys') IS NULL
  RAISERROR('  Always Encrypted keys skipped: they need SQL Server 2016', 10, 1) WITH NOWAIT
ELSE
BEGIN
  IF NOT EXISTS (SELECT 1 FROM sys.column_master_keys WHERE [name] = N'CustomerCMK')
    EXEC(N'CREATE COLUMN MASTER KEY [CustomerCMK] WITH (KEY_STORE_PROVIDER_NAME = N''MSSQL_CERTIFICATE_STORE'', KEY_PATH = N''CurrentUser/My/A66BB0F6DD70BDFF02B62D0F87E340288E6F9305'')')
  IF NOT EXISTS (SELECT 1 FROM sys.column_encryption_keys WHERE [name] = N'CustomerCEK')
    EXEC(N'CREATE COLUMN ENCRYPTION KEY [CustomerCEK] WITH VALUES (COLUMN_MASTER_KEY = [CustomerCMK], ALGORITHM = ''RSA_OAEP'', ENCRYPTED_VALUE = 0x016E000001630075007200720065006E0074007500730065007200)')
END
