// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

﻿using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
internal class TableQuench_ModifyConstraintTests : BaseTableQuenchTests
{
    [Test]
    public void TableQuench_ShouldModifyDefault()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping(COLUMN_DEFAULT) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_Name = 'ModifyMyDefault' AND COLUMN_NAME = 'Id'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("0"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyColumnLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping([definition]) FROM sys.check_constraints ck WITH (NOLOCK) WHERE ck.[parent_object_id] = OBJECT_ID('dbo.ModifyMyColumnCheck')  AND COL_NAME(ck.parent_object_id, ck.parent_column_id) = 'Id'";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Id]<(10)"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyTableLevelCheckConstraint()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT SchemaSmith.fn_StripParenWrapping([definition]) FROM sys.check_constraints ck WITH (NOLOCK) WHERE ck.[object_id] = OBJECT_ID('dbo.CHK_ModifyMyTableCheck_MyCheck')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("[Id]<[Col2]"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForColumnChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COL_NAME(fc.[parent_object_id], fc.parent_column_id) FROM sys.foreign_key_columns fc WITH (NOLOCK) WHERE fc.[constraint_object_id] = OBJECT_ID('dbo.FK_ModifyFKColumn_ModifyFKColumnRef')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("Col2"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForReferenceColumnChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT COL_NAME(fc.[referenced_object_id], fc.referenced_column_id) FROM sys.foreign_key_columns fc WITH (NOLOCK) WHERE fc.[constraint_object_id] = OBJECT_ID('dbo.FK_ModifyFKReferenceColumn_ModifyFKReferenceColumnRef')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("RefCol"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForReferenceTableChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT OBJECT_NAME(fc.[referenced_object_id]) FROM sys.foreign_key_columns fc WITH (NOLOCK) WHERE fc.[constraint_object_id] = OBJECT_ID('dbo.FK_ModifyFKReferenceTable_ModifyFKReferenceTableRef')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("ModifyFKReferenceTableRefNew"));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForCascadeDeleteChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT delete_referential_action FROM sys.foreign_keys fk WITH (NOLOCK) WHERE fk.[object_id] = OBJECT_ID('dbo.FK_ModifyFKCascadeDelete_ModifyFKCascadeDeleteRef')";
        Assert.That((byte?)cmd.ExecuteScalar(), Is.EqualTo((byte)0));
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldModifyForeignKeyForCascadeUpdateChange()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT update_referential_action FROM sys.foreign_keys fk WITH (NOLOCK) WHERE fk.[object_id] = OBJECT_ID('dbo.FK_ModifyFKCascadeUpdate_ModifyFKCascadeUpdateRef')";
        Assert.That((byte?)cmd.ExecuteScalar(), Is.EqualTo((byte)0));
        conn.Close();
    }

    [OneTimeSetUp]
    public void SetUp()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
--TableQuench_ShouldModifyDefault
CREATE TABLE dbo.ModifyMyDefault (Id INT NOT NULL DEFAULT 10)
--TableQuench_ShouldModifyColumnLevelCheckConstraint
CREATE TABLE dbo.ModifyMyColumnCheck (Id INT NOT NULL CHECK ([Id]<(20)))
--TableQuench_ShouldModifyTableLevelCheckConstraint
CREATE TABLE dbo.ModifyMyTableCheck (Id INT NOT NULL, Col2 INT)
ALTER TABLE dbo.ModifyMyTableCheck ADD CONSTRAINT CHK_ModifyMyTableCheck_MyCheck CHECK ([Col2]>[Id])
--TableQuench_ShouldModifyForeignKeyForColumnChange
CREATE TABLE dbo.ModifyFKColumn (Id INT NOT NULL PRIMARY KEY, Col2 INT, Col3 INT)
CREATE TABLE dbo.ModifyFKColumnRef (Id INT NOT NULL PRIMARY KEY)
ALTER TABLE dbo.ModifyFKColumn ADD CONSTRAINT FK_ModifyFKColumn_ModifyFKColumnRef FOREIGN KEY (Col3) REFERENCES dbo.ModifyFKColumnRef (Id)
--TableQuench_ShouldModifyForeignKeyForReferenceColumnChange
CREATE TABLE dbo.ModifyFKReferenceColumn (Id INT NOT NULL PRIMARY KEY, Col2 INT, Col3 INT)
CREATE TABLE dbo.ModifyFKReferenceColumnRef (Id INT NOT NULL PRIMARY KEY, RefCol INT NOT NULL)
CREATE UNIQUE INDEX IDX_RefKey ON dbo.ModifyFKReferenceColumnRef (RefCol)
ALTER TABLE dbo.ModifyFKReferenceColumn ADD CONSTRAINT FK_ModifyFKReferenceColumn_ModifyFKReferenceColumnRef FOREIGN KEY (Col3) REFERENCES dbo.ModifyFKReferenceColumnRef (Id)
CREATE TABLE dbo.ModifyFKReferenceTable (Id INT NOT NULL PRIMARY KEY, Col2 INT, Col3 INT)
--TableQuench_ShouldModifyForeignKeyForReferenceTableChange
CREATE TABLE dbo.ModifyFKReferenceTableRef (Id INT NOT NULL PRIMARY KEY)
CREATE TABLE dbo.ModifyFKReferenceTableRefNew (Id INT NOT NULL PRIMARY KEY)
ALTER TABLE dbo.ModifyFKReferenceTable ADD CONSTRAINT FK_ModifyFKReferenceTable_ModifyFKReferenceTableRef FOREIGN KEY (Col3) REFERENCES dbo.ModifyFKReferenceTableRef (Id)
--TableQuench_ShouldModifyForeignKeyForCascadeDeleteChange
CREATE TABLE dbo.ModifyFKCascadeDelete (Id INT NOT NULL PRIMARY KEY, Col2 INT, Col3 INT)
CREATE TABLE dbo.ModifyFKCascadeDeleteRef (Id INT NOT NULL PRIMARY KEY)
ALTER TABLE dbo.ModifyFKCascadeDelete ADD CONSTRAINT FK_ModifyFKCascadeDelete_ModifyFKCascadeDeleteRef FOREIGN KEY (Col3) REFERENCES dbo.ModifyFKCascadeDeleteRef (Id) ON DELETE CASCADE
--TableQuench_ShouldModifyForeignKeyForCascadeUpdateChange
CREATE TABLE dbo.ModifyFKCascadeUpdate (Id INT NOT NULL PRIMARY KEY, Col2 INT, Col3 INT)
CREATE TABLE dbo.ModifyFKCascadeUpdateRef (Id INT NOT NULL PRIMARY KEY)
ALTER TABLE dbo.ModifyFKCascadeUpdate ADD CONSTRAINT FK_ModifyFKCascadeUpdate_ModifyFKCascadeUpdateRef FOREIGN KEY (Col3) REFERENCES dbo.ModifyFKCascadeUpdateRef (Id) ON UPDATE CASCADE
--TableQuench_ShouldApplyForeignKeySetNullAction
CREATE TABLE dbo.ApplyFKSetNull (Id INT NOT NULL PRIMARY KEY, Col2 INT)
CREATE TABLE dbo.ApplyFKSetNullRef (Id INT NOT NULL PRIMARY KEY)
ALTER TABLE dbo.ApplyFKSetNull ADD CONSTRAINT FK_ApplyFKSetNull_Ref FOREIGN KEY (Col2) REFERENCES dbo.ApplyFKSetNullRef (Id)
--TableQuench_ShouldApplyForeignKeyCascadeAction
CREATE TABLE dbo.ApplyFKCascade (Id INT NOT NULL PRIMARY KEY, Col2 INT)
CREATE TABLE dbo.ApplyFKCascadeRef (Id INT NOT NULL PRIMARY KEY)
ALTER TABLE dbo.ApplyFKCascade ADD CONSTRAINT FK_ApplyFKCascade_Ref FOREIGN KEY (Col2) REFERENCES dbo.ApplyFKCascadeRef (Id)
";
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();

        var json = """
        [
            {
                "Schema": "[dbo]",
                "Name": "[ModifyMyDefault]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false,
                      "Default": "0"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyMyColumnCheck]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false,
                      "CheckExpression": "[Id]<(10)"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyMyTableCheck]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "CheckConstraints": [
                    {
                      "Name": "CHK_ModifyMyTableCheck_MyCheck",
                      "Expression": "[Id]<[Col2]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFKColumn]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "[Col3]",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_ModifyFKColumn_ModifyFKColumnRef]",
                      "Columns": "[Col2]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[ModifyFKColumnRef]",
                      "RelatedColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFKReferenceColumn]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "[Col3]",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_ModifyFKReferenceColumn_ModifyFKReferenceColumnRef]",
                      "Columns": "[Col3]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[ModifyFKReferenceColumnRef]",
                      "RelatedColumns": "[RefCol]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFKReferenceTable]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "[Col3]",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_ModifyFKReferenceTable_ModifyFKReferenceTableRef]",
                      "Columns": "[Col3]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[ModifyFKReferenceTableRefNew]",
                      "RelatedColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFKCascadeDelete]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "[Col3]",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_ModifyFKCascadeDelete_ModifyFKCascadeDeleteRef]",
                      "Columns": "[Col2]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[ModifyFKCascadeDeleteRef]",
                      "RelatedColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ModifyFKCascadeUpdate]",
                "Columns": [
                    {
                      "Name": "[Id]",
                      "DataType": "INT",
                      "Nullable": false
                    },
                    {
                      "Name": "[Col2]",
                      "DataType": "INT",
                      "Nullable": true
                    },
                    {
                      "Name": "[Col3]",
                      "DataType": "INT",
                      "Nullable": true
                    }
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_ModifyFKCascadeUpdate_ModifyFKCascadeUpdateRef]",
                      "Columns": "[Col2]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[ModifyFKCascadeUpdateRef]",
                      "RelatedColumns": "[Id]"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ApplyFKSetNull]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Col2]", "DataType": "INT", "Nullable": true}
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_ApplyFKSetNull_Ref]",
                      "Columns": "[Col2]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[ApplyFKSetNullRef]",
                      "RelatedColumns": "[Id]",
                      "DeleteAction": "SET NULL"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ApplyFKSetNullRef]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false}
                ],
                "Indexes": [
                    {"Name": "[PK_ApplyFKSetNullRef]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]"}
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ApplyFKCascade]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false},
                    {"Name": "[Col2]", "DataType": "INT", "Nullable": true}
                ],
                "ForeignKeys": [
                    {
                      "Name": "[FK_ApplyFKCascade_Ref]",
                      "Columns": "[Col2]",
                      "RelatedTableSchema": "dbo",
                      "RelatedTable": "[ApplyFKCascadeRef]",
                      "RelatedColumns": "[Id]",
                      "DeleteAction": "CASCADE",
                      "UpdateAction": "CASCADE"
                    }
                ]
            },
            {
                "Schema": "[dbo]",
                "Name": "[ApplyFKCascadeRef]",
                "Columns": [
                    {"Name": "[Id]", "DataType": "INT", "Nullable": false}
                ],
                "Indexes": [
                    {"Name": "[PK_ApplyFKCascadeRef]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[Id]"}
                ]
            }
        ]
        """;
        RunTableQuenchProc(cmd, json);

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldApplyForeignKeySetNullAction()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Setup FK was created with NO ACTION (default). Quench applied SET NULL.
        // sys.foreign_keys: 0=NO ACTION, 1=CASCADE, 2=SET NULL, 3=SET DEFAULT
        cmd.CommandText = "SELECT delete_referential_action FROM sys.foreign_keys fk WITH (NOLOCK) WHERE fk.[object_id] = OBJECT_ID('dbo.FK_ApplyFKSetNull_Ref')";
        Assert.That((byte?)cmd.ExecuteScalar(), Is.EqualTo((byte)2), "DeleteAction should be SET NULL (2)");
        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldApplyForeignKeyCascadeAction()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();

        // Setup FK was created with NO ACTION (default). Quench applied CASCADE for both delete and update.
        cmd.CommandText = "SELECT delete_referential_action FROM sys.foreign_keys fk WITH (NOLOCK) WHERE fk.[object_id] = OBJECT_ID('dbo.FK_ApplyFKCascade_Ref')";
        Assert.That((byte?)cmd.ExecuteScalar(), Is.EqualTo((byte)1), "DeleteAction should be CASCADE (1)");

        cmd.CommandText = "SELECT update_referential_action FROM sys.foreign_keys fk WITH (NOLOCK) WHERE fk.[object_id] = OBJECT_ID('dbo.FK_ApplyFKCascade_Ref')";
        Assert.That((byte?)cmd.ExecuteScalar(), Is.EqualTo((byte)1), "UpdateAction should be CASCADE (1)");
        conn.Close();
    }
}
