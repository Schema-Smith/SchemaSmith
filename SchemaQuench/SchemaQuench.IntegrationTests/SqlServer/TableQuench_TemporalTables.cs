// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using System;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.All)]
public class TableQuench_TemporalTables : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldCreateNewTemporalTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(COLUMNPROPERTY(OBJECT_ID('dbo.MyNewTeporalTable'), 'ValidFrom', 'GeneratedAlwaysType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(1));

        cmd.CommandText = "SELECT CAST(COLUMNPROPERTY(OBJECT_ID('dbo.MyNewTeporalTable'), 'ValidTo', 'GeneratedAlwaysType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(2));

        cmd.CommandText = "SELECT CAST(OBJECTPROPERTY(OBJECT_ID('dbo.MyNewTeporalTable'), 'TableTemporalType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(2));

        cmd.CommandText = "SELECT CAST(OBJECTPROPERTY(OBJECT_ID('dbo.MyNewTeporalTable_Hist'), 'TableTemporalType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(1));

        conn.Close();
    }

    // A temporal table's period columns are the engine's; a package declares the table temporal without listing them.
    // The drop-by-absence pass exempted them by name (ValidFrom/ValidTo), so a table with its own period names had
    // them dropped -- which the engine refuses -- and the deploy failed. They are exempt by being period columns.
    [Test]
    public void TableQuench_KeepsCustomNamedPeriodColumns()
    {
        var table = $"TemporalCustom_{Guid.NewGuid():N}"[..28];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $@"CREATE TABLE dbo.[{table}] (Id INT NOT NULL PRIMARY KEY, V INT NULL,
  SysStart DATETIME2(2) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
  SysEnd DATETIME2(2) GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
  PERIOD FOR SYSTEM_TIME (SysStart, SysEnd))
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.[{table}_Hist]));";
        cmd.ExecuteNonQuery();
        try
        {
            RunTableQuenchProc(cmd, $$"""
[ { "Schema": "[dbo]", "Name": "[{{table}}]", "IsTemporal": true,
    "Columns": [ { "Name": "[Id]", "DataType": "INT", "Nullable": false }, { "Name": "[V]", "DataType": "INT", "Nullable": true } ],
    "Indexes": [ { "Name": "[PK_{{table}}]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[Id]" } ] } ]
""");

            cmd.CommandText = $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name IN ('SysStart', 'SysEnd')";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(2), "the period columns must survive the deploy");
        }
        finally
        {
            cmd.CommandText = $"IF OBJECT_ID('dbo.[{table}]') IS NOT NULL BEGIN ALTER TABLE dbo.[{table}] SET (SYSTEM_VERSIONING = OFF); DROP TABLE dbo.[{table}]; DROP TABLE dbo.[{table}_Hist]; END";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void TableQuench_ShouldRemoveTemporalTracking()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(COLUMNPROPERTY(OBJECT_ID('dbo.MyOldTeporalTable'), 'ValidFrom', 'GeneratedAlwaysType') AS INT)";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(DBNull.Value));

        cmd.CommandText = "SELECT CAST(COLUMNPROPERTY(OBJECT_ID('dbo.MyOldTeporalTable'), 'ValidTo', 'GeneratedAlwaysType') AS INT)";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(DBNull.Value));

        cmd.CommandText = "SELECT CAST(OBJECTPROPERTY(OBJECT_ID('dbo.MyOldTeporalTable'), 'TableTemporalType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(0));

        cmd.CommandText = "SELECT CAST(OBJECTPROPERTY(OBJECT_ID('dbo.MyOldTeporalTable_Hist'), 'TableTemporalType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(0));

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldMakeExistingTableTemporal()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT CAST(COLUMNPROPERTY(OBJECT_ID('dbo.MyUpdatedTeporalTable'), 'ValidFrom', 'GeneratedAlwaysType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(1));

        cmd.CommandText = "SELECT CAST(COLUMNPROPERTY(OBJECT_ID('dbo.MyUpdatedTeporalTable'), 'ValidTo', 'GeneratedAlwaysType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(2));

        cmd.CommandText = "SELECT CAST(OBJECTPROPERTY(OBJECT_ID('dbo.MyUpdatedTeporalTable'), 'TableTemporalType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(2));

        cmd.CommandText = "SELECT CAST(OBJECTPROPERTY(OBJECT_ID('dbo.MyUpdatedTeporalTable_Hist'), 'TableTemporalType') AS INT)";
        Assert.That(cmd.ExecuteScalar() as int?, Is.EqualTo(1));

        conn.Close();
    }

    [Test]
    public void ShouldAlterTemporalTable()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        Assert.That(GetColumnDataType(cmd, "MyAlteredTemporalTable", "Somedata"), Is.EqualTo("VARCHAR(500)"));
        Assert.That(GetColumnDataType(cmd, "MyAlteredTemporalTable_Hist", "Somedata"), Is.EqualTo("VARCHAR(500)"));

        Assert.That(GetColumnDataType(cmd, "MyAlteredTemporalTable", "NewCol"), Is.EqualTo("INT"));
        Assert.That(GetColumnDataType(cmd, "MyAlteredTemporalTable_Hist", "NewCol"), Is.EqualTo("INT"));

        conn.Close();
    }

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
--TableQuench_ShouldRemoveTemporalTracking
CREATE TABLE [dbo].[MyOldTeporalTable] 
  ([Id] INT NOT NULL, 
   [Somedata] VARCHAR(500) NOT NULL, 
   [ValidFrom] DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
   [ValidTo] DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
   CONSTRAINT [PK_MyOldTeporalTable] PRIMARY KEY NONCLUSTERED  ([Id]),
   PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
  ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[MyOldTeporalTable_Hist]))
--TableQuench_ShouldMakeExistingTableTemporal
CREATE TABLE [dbo].[MyUpdatedTeporalTable] 
  ([Id] INT NOT NULL, 
   [Somedata] VARCHAR(500) NOT NULL, 
   CONSTRAINT [PK_MyUpdatedTeporalTable] PRIMARY KEY NONCLUSTERED  ([Id])
  )
INSERT [dbo].[MyUpdatedTeporalTable] ([Id], [Somedata]) VALUES(1, 'xx'), (2, 'yy')
--ShouldAlterTemporalTable
CREATE TABLE [dbo].[MyAlteredTemporalTable] 
  ([Id] INT NOT NULL, 
   [Somedata] VARCHAR(200) NOT NULL, 
   [ValidFrom] DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
   [ValidTo] DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
   CONSTRAINT [PK_MyAlteredTemporalTable] PRIMARY KEY NONCLUSTERED  ([Id]),
   PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
  ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[MyAlteredTemporalTable_Hist]))
""";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
            [
            {
                "Schema": "[dbo]",
                "Name": "[MyNewTeporalTable]",
                "IsTemporal": true,
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Somedata]",
                      "DataType": "VARCHAR(500)",
                      "Nullable": false
                    }
                            ],
                "Indexes": [
                    {
                      "Name": "[PK_MyNewTeporalTable]",
                      "PrimaryKey": true,
                      "IndexColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[MyOldTeporalTable]",
                "IsTemporal": false,
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Somedata]",
                      "DataType": "VARCHAR(500)",
                      "Nullable": false
                    }
                            ],
                "Indexes": [
                    {
                      "Name": "[PK_MyOldTeporalTable]",
                      "PrimaryKey": true,
                      "IndexColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[MyUpdatedTeporalTable]",
                "IsTemporal": true,
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Somedata]",
                      "DataType": "VARCHAR(500)",
                      "Nullable": false
                    }
                            ],
                "Indexes": [
                    {
                      "Name": "[PK_MyUpdatedTeporalTable]",
                      "PrimaryKey": true,
                      "IndexColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[MyAlteredTemporalTable]",
                "IsTemporal": true,
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Somedata]",
                      "DataType": "VARCHAR(500)",
                      "Nullable": false
                    },
                    {
                      "Name": "[NewCol]",
                      "DataType": "INT",
                      "Nullable": false
                    }
                            ],
                "Indexes": [
                    {
                      "Name": "[PK_MyAlteredTemporalTable]",
                      "PrimaryKey": true,
                      "IndexColumns": "[Id]"
                    }
                ]
            }
            ]
            """;
        RunTableQuenchProc(cmd, json);
    }
}
