CREATE OR ALTER VIEW [{{SchemaName}}].[ActiveCustomers]
AS
SELECT c.[CustomerID],
       c.[CustomerName],
       c.[Email],
       c.[CountryCode],
       ctry.[CountryName],
       c.[CreatedAt],
       c.[LastModifiedAt]
  FROM [{{SchemaName}}].[Customers] c
  LEFT JOIN [dbo].[Countries] ctry ON ctry.[Code] = c.[CountryCode]
 WHERE c.[LastModifiedAt] >= DATEADD(DAY, -30, SYSUTCDATETIME());
