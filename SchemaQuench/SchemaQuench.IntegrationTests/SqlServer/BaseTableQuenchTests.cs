// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using System;
using System.Data;
using System.Threading;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Microsoft.Extensions.Configuration;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class BaseTableQuenchTests
{
    protected readonly string _connectionString;
    protected readonly string _mainDb;
    protected readonly string _productName = "Quench Table Tests";

    public BaseTableQuenchTests()
    {
        var config = FactoryContainer.Resolve<IConfigurationRoot>();
        var connProps = ConnectionString.ReadProperties(config, "Target:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["Target:Server"], "master", config["Target:User"], config["Target:Password"], config["Target:Port"], connProps);
        _mainDb = config["ScriptTokens:MainDB"];
    }

    protected void RunTableQuenchProc(IDbCommand cmd, string json, bool indexOnly = false, bool dropTablesRemovedFromProduct = false, bool whatIf = false, string productName = "")
    {
        var prod = string.IsNullOrEmpty(productName) ? _productName : productName;
        cmd.CommandTimeout = 300;
        cmd.CommandText = indexOnly
            ? $"EXEC SchemaSmith.IndexOnlyQuench @ProductName = '{prod}', @TableDefinitions = '{json.Replace("'", "''")}', @DropUnknownIndexes = 1"
            : $"EXEC SchemaSmith.TableQuench @ProductName = '{prod}', @TableDefinitions = '{json.Replace("'", "''")}', @WhatIf = {(whatIf ? 1 : 0)}, @DropTablesRemovedFromProduct = {(dropTablesRemovedFromProduct ? 1 : 0)}, @DropUnknownIndexes = 0";
        var retry = true;
        var tries = 0;
        while (retry && tries++ < 10)
        {
            try
            {
                cmd.ExecuteNonQuery();
                retry = false;
            }
            catch (Exception e)
            {
                if (!e.Message.ContainsIgnoringCase("has been chosen as the deadlock victim")) throw;
                Thread.Sleep(1000);
            }
        }
    }

    protected static string GetColumnDataType(IDbCommand cmd, string tableName, string columnName)
    {
        // Calls the same SchemaSmith.fn_ColumnTypeArguments the product extraction/drift-comparison
        // procs use, rather than re-implementing the type-argument CASE a third time here — a fourth
        // hand-maintained copy is exactly the pattern that let TIME(3)/DATETIMEOFFSET(3) go unnoticed.
        cmd.CommandText = @$"
SELECT UPPER(USER_TYPE) + SchemaSmith.fn_ColumnTypeArguments(USER_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION,
                               CASE WHEN sc.xml_collection_id <> 0
                                    THEN (SELECT CASE WHEN sc.is_xml_document = 1 THEN 'DOCUMENT ' ELSE '' END + '[' + SCHEMA_NAME(xc.[schema_id]) + '].[' + xc.[name] + ']' FROM sys.xml_schema_collections xc WHERE xc.xml_collection_id = sc.xml_collection_id)
                                    END,
                               sc.is_rowguidcol) +
                          CASE WHEN ident.column_id IS NOT NULL
                               THEN ' IDENTITY(' + CONVERT(NVARCHAR(20), ident.seed_value) + ', ' + CONVERT(NVARCHAR(20), ident.increment_value) + ')' +
                                    CASE WHEN ident.is_not_for_replication = 1 THEN ' NOT FOR REPLICATION' ELSE '' END
                               ELSE '' END
  FROM INFORMATION_SCHEMA.COLUMNS c WITH (NOLOCK)
  JOIN sys.columns sc WITH (NOLOCK) ON sc.[object_id] = OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME) AND sc.[name] = c.COLUMN_NAME
  JOIN (SELECT CASE WHEN SCHEMA_NAME(st.[schema_id]) IN ('sys', 'dbo')
                    THEN '' ELSE SCHEMA_NAME(st.[schema_id]) + '.' END + st.[name] AS USER_TYPE, st.user_type_id
          FROM sys.types st WITH (NOLOCK)) st ON st.user_type_id = sc.user_type_id
  LEFT JOIN sys.identity_columns ident WITH (NOLOCK) ON ident.[Name] = c.COLUMN_NAME
                                                    AND ident.[object_id] = OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME)
  WHERE TABLE_SCHEMA = 'dbo' 
    AND TABLE_NAME = '{tableName}'
    AND COLUMN_NAME = '{columnName}'";
        return cmd.ExecuteScalar()!.ToString()!.ToUpper();
    }
}
