// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.SqlServer;

/// <summary>
/// A column's length, precision and scale must survive extraction. Two ways that failed: in a case-sensitive database the type name ('decimal') never matched
/// the upper-case list of types that take arguments, so <c>decimal(10,4)</c> extracted as <c>DECIMAL</c> and deployed
/// as <c>decimal(18,0)</c>; and an alias type whose name ends in CHAR or BINARY was given a width, which SQL Server
/// refuses ("Cannot specify a column width on data type"). The database is this fixture's own because its collation
/// is what is under test.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public class TypeArgumentExtractionTests
{
    private IDbConnection _connection = null!;
    private readonly string _db = $"TypeArgsCs_{Guid.NewGuid():N}"[..24];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(FixtureSetup.GetMainDbConnectionString());
        _connection.Open();
        Exec($"CREATE DATABASE [{_db}] COLLATE Latin1_General_CS_AS");
        _connection.ChangeDatabase(_db);
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = "SELECT CONVERT(INT, SERVERPROPERTY('ProductMajorVersion'))";
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer, serverMajorVersion: Convert.ToInt32(cmd.ExecuteScalar()));

        Exec("CREATE TYPE dbo.ShortChar FROM varchar(10)");
        Exec(@"CREATE TABLE dbo.TypeArgs (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(10,4) NULL, Label VARCHAR(20) NULL,
                 Code NCHAR(3) NULL, Stamp DATETIME2(3) NULL, Blob VARBINARY(MAX) NULL, Alias dbo.ShortChar NULL)");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null) return;
        try
        {
            _connection.ChangeDatabase("master");
            using var cmd = _connection.CreateCommand();
            TestDatabaseDrop.Drop(cmd, _db);
        }
        finally
        {
            _connection.Close();
            _connection.Dispose();
        }
    }

    [Test]
    public void InACaseSensitiveDatabase_TypeArgumentsAreExtracted()
    {
        var json = Extract("TypeArgs");
        var types = JObject.Parse(json)["Columns"]!.ToDictionary(c => (string)c["Name"]!, c => (string)c["DataType"]!);

        Assert.Multiple(() =>
        {
            Assert.That(types["[Amount]"], Is.EqualTo("DECIMAL(10, 4)"), json);
            Assert.That(types["[Label]"], Is.EqualTo("VARCHAR(20)"), json);
            Assert.That(types["[Code]"], Is.EqualTo("NCHAR(3)"), json);
            Assert.That(types["[Stamp]"], Is.EqualTo("DATETIME2(3)"), json);
            Assert.That(types["[Blob]"], Is.EqualTo("VARBINARY(MAX)"), json);
            Assert.That(types["[Alias]"], Is.EqualTo("ShortChar"), "an alias type is written as created, with no width\n" + json);
        });
    }

    private string Extract(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = '{table}'";
        using var reader = cmd.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
            if (!reader.IsDBNull(0)) sb.Append(reader.GetString(0));
        return sb.ToString();
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }
}
