IF OBJECT_ID('[{{SchemaName}}].[SetupMarker]', 'U') IS NULL CREATE TABLE [{{SchemaName}}].[SetupMarker] (Ran INT NOT NULL);
INSERT INTO [{{SchemaName}}].[SetupMarker] (Ran) VALUES (1);
