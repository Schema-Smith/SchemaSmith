INSERT INTO ag_lab.dbo.RunLog (Folder, ServerName) VALUES ('Secondary', CAST(SERVERPROPERTY('ServerName') AS sysname));
