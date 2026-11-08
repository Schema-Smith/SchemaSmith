IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'Security')
EXEC sys.sp_executesql N'CREATE SCHEMA [Security]'
