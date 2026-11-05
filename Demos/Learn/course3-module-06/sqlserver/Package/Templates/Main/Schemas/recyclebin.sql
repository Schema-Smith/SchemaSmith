IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'recyclebin')
BEGIN
  EXEC sys.sp_executesql N'CREATE SCHEMA [recyclebin]'
END

