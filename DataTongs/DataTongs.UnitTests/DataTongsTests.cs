// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.UnitTests;

[TestFixture]
public class DataTongsTests
{
    #region ParseTableName Tests

    [Test]
    public void ParseTableName_SqlServer_TwoPartName_ReturnsSchemaAndTable()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var result = dt.ParseTableName("dbo.Users");
        Assert.Multiple(() =>
        {
            Assert.That(result.Schema, Is.EqualTo("dbo"));
            Assert.That(result.Name, Is.EqualTo("Users"));
        });
    }

    [Test]
    public void ParseTableName_SqlServer_SingleName_DefaultsToDbo()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var result = dt.ParseTableName("Users");
        Assert.Multiple(() =>
        {
            Assert.That(result.Schema, Is.EqualTo("dbo"));
            Assert.That(result.Name, Is.EqualTo("Users"));
        });
    }

    [Test]
    public void ParseTableName_PostgreSQL_TwoPartName_ReturnsSchemaAndTable()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var result = dt.ParseTableName("public.users");
        Assert.Multiple(() =>
        {
            Assert.That(result.Schema, Is.EqualTo("public"));
            Assert.That(result.Name, Is.EqualTo("users"));
        });
    }

    [Test]
    public void ParseTableName_PostgreSQL_SingleName_DefaultsToPublic()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var result = dt.ParseTableName("users");
        Assert.Multiple(() =>
        {
            Assert.That(result.Schema, Is.EqualTo("public"));
            Assert.That(result.Name, Is.EqualTo("users"));
        });
    }

    [Test]
    public void ParseTableName_MySQL_SingleName_ReturnsEmptySchema()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var result = dt.ParseTableName("users");
        Assert.Multiple(() =>
        {
            Assert.That(result.Schema, Is.EqualTo(""));
            Assert.That(result.Name, Is.EqualTo("users"));
        });
    }

    [Test]
    public void ParseTableName_MySQL_TwoPartName_StripsDbPrefixReturnsTableOnly()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var result = dt.ParseTableName("mydb.users");
        Assert.Multiple(() =>
        {
            Assert.That(result.Schema, Is.EqualTo(""));
            Assert.That(result.Name, Is.EqualTo("users"));
        });
    }

    [Test]
    public void ParseTableName_TrimsWhitespace()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var result = dt.ParseTableName(" dbo . Users ");
        Assert.Multiple(() =>
        {
            Assert.That(result.Schema, Is.EqualTo("dbo"));
            Assert.That(result.Name, Is.EqualTo("Users"));
        });
    }

    #endregion

    #region FormatOrderColumns Tests

    [Test]
    public void FormatOrderColumns_SqlServer_UseBrackets()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var result = dt.FormatOrderColumns("[Id],[Name]");
        Assert.That(result, Is.EqualTo("[Id],[Name]"));
    }

    [Test]
    public void FormatOrderColumns_SqlServer_StripsStarPrefix()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var result = dt.FormatOrderColumns("*[Id]");
        Assert.That(result, Is.EqualTo("[Id]"));
    }

    [Test]
    public void FormatOrderColumns_PostgreSQL_UseDoubleQuotes()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var result = dt.FormatOrderColumns("\"id\",\"name\"");
        Assert.That(result, Is.EqualTo("\"id\",\"name\""));
    }

    [Test]
    public void FormatOrderColumns_MySQL_UseBackticks()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var result = dt.FormatOrderColumns("`id`,`name`");
        Assert.That(result, Is.EqualTo("`id`,`name`"));
    }

    [Test]
    public void FormatOrderColumns_SqlServer_UnquotedColumns_AddsBrackets()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var result = dt.FormatOrderColumns("Id, Name");
        Assert.That(result, Is.EqualTo("[Id],[Name]"));
    }

    #endregion

    #region IsValidKeyColumns Tests

    [Test]
    public void IsValidKeyColumns_ValidCommaSeparated_ReturnsTrue()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("Col1,Col2"), Is.True);
    }

    [Test]
    public void IsValidKeyColumns_SingleColumn_ReturnsTrue()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("Id"), Is.True);
    }

    [Test]
    public void IsValidKeyColumns_BracketQuotedColumns_ReturnsTrue()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("[Col1],[Col2]"), Is.True);
    }

    [Test]
    public void IsValidKeyColumns_DoubleQuotedColumns_ReturnsTrue()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("\"Col1\",\"Col2\""), Is.True);
    }

    [Test]
    public void IsValidKeyColumns_BacktickQuotedColumns_ReturnsTrue()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("`Col1`,`Col2`"), Is.True);
    }

    [Test]
    public void IsValidKeyColumns_JsonArray_ReturnsFalse()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("[\"Col1\",\"Col2\"]"), Is.False);
    }

    [Test]
    public void IsValidKeyColumns_EmptyJsonArray_ReturnsFalse()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("[]"), Is.False);
    }

    [Test]
    public void IsValidKeyColumns_EmptySegment_ReturnsFalse()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("Col1, ,Col2"), Is.False);
    }

    [Test]
    public void IsValidKeyColumns_TrailingComma_ReturnsFalse()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("Col1,"), Is.False);
    }

    [Test]
    public void IsValidKeyColumns_Null_ReturnsFalse()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns(null!), Is.False);
    }

    [Test]
    public void IsValidKeyColumns_WhitespaceOnly_ReturnsFalse()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("   "), Is.False);
    }

    [Test]
    public void IsValidKeyColumns_ColumnsWithSpaces_ReturnsTrue()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("Col1, Col2"), Is.True);
    }

    [Test]
    public void IsValidKeyColumns_StarPrefixed_ReturnsTrue()
    {
        Assert.That(global::DataTongs.DataTongs.IsValidKeyColumns("*[Id],*[Name]"), Is.True);
    }

    #endregion

    #region TableExists Tests

    [Test]
    public void TableExists_SqlServer_UsesObjectId()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(true);

        var result = dt.TableExists(cmd, "dbo", "Users");
        Assert.That(result, Is.True);
        Assert.That(cmd.CommandText, Does.Contain("OBJECT_ID"));
    }

    [Test]
    public void TableExists_SqlServer_NotExists_ReturnsFalse()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(false);

        var result = dt.TableExists(cmd, "dbo", "Users");
        Assert.That(result, Is.False);
    }

    [Test]
    public void TableExists_PostgreSQL_UsesPgClass()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(true);

        var result = dt.TableExists(cmd, "public", "users");
        Assert.That(result, Is.True);
        Assert.That(cmd.CommandText, Does.Contain("pg_class"));
    }

    [Test]
    public void TableExists_MySQL_UsesInformationSchema()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(1L);

        var result = dt.TableExists(cmd, "testdb", "users");
        Assert.That(result, Is.True);
        Assert.That(cmd.CommandText, Does.Contain("INFORMATION_SCHEMA.TABLES"));
    }

    [Test]
    public void TableExists_MySQL_NotExists_ReturnsFalse()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(0L);

        var result = dt.TableExists(cmd, "testdb", "users");
        Assert.That(result, Is.False);
    }

    [Test]
    public void TableExists_MySQL_StripsBackticks()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(1L);

        dt.TableExists(cmd, "`testdb`", "`users`");
        Assert.That(cmd.CommandText, Does.Contain("'testdb'"));
        Assert.That(cmd.CommandText, Does.Contain("'users'"));
    }

    [Test]
    public void TableExists_MySQL_EscapesSingleQuotes()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(1L);

        dt.TableExists(cmd, "testdb", "user's_table");
        Assert.That(cmd.CommandText, Does.Contain("user''s_table"));
    }

    #endregion

    #region GetSelectColumns Tests

    [Test]
    public void GetSelectColumns_SqlServer_QueriesInformationSchema()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[Id],[Name]");

        var result = dt.GetSelectColumns(cmd, "dbo", "Users");
        Assert.That(result, Is.EqualTo("[Id],[Name]"));
        Assert.That(cmd.CommandText, Does.Contain("INFORMATION_SCHEMA.COLUMNS"));
    }

    [Test]
    public void GetSelectColumns_PostgreSQL_QueriesInformationSchema()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("\"id\",\"name\"");

        var result = dt.GetSelectColumns(cmd, "public", "users");
        Assert.That(result, Is.EqualTo("\"id\",\"name\""));
        Assert.That(cmd.CommandText, Does.Contain("information_schema.columns"));
    }

    [Test]
    public void GetSelectColumns_MySQL_QueriesInformationSchema()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("`id`,`name`");

        var result = dt.GetSelectColumns(cmd, "testdb", "users");
        Assert.That(result, Is.EqualTo("`id`,`name`"));
        Assert.That(cmd.CommandText, Does.Contain("INFORMATION_SCHEMA.COLUMNS"));
    }

    [Test]
    public void GetSelectColumns_SqlServer_HandlesGeography()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[Location].ToString() AS [Location], [Location].STSrid AS [Location.STSrid]");

        var result = dt.GetSelectColumns(cmd, "dbo", "Locations");
        Assert.That(result, Does.Contain("Location"));
    }

    [Test]
    public void GetSelectColumns_SqlServer_HandlesGeometry()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[Shape].ToString() AS [Shape], [Shape].STSrid AS [Shape.STSrid]");

        var result = dt.GetSelectColumns(cmd, "dbo", "Shapes");
        Assert.That(result, Does.Contain("Shape"));
        // Verify the query checks for both GEOGRAPHY and GEOMETRY
        Assert.That(cmd.CommandText, Does.Contain("'GEOGRAPHY'"));
        Assert.That(cmd.CommandText, Does.Contain("'GEOMETRY'"));
    }

    // SQL Server has always paired a spatial column's WKT with an [Col.STSrid] companion, and the shred
    // consumes it. PostgreSQL and MySQL extracted the WKT alone, so a spatial value delivered into an
    // untyped destination column silently lost its reference system -- right coordinates, wrong CRS, no
    // error. These pin the companion into the other two extractors so all four engines agree.
    [Test]
    public void GetSelectColumns_PostgreSQL_EmitsSridCompanionForSpatial()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("ST_AsText(\"shape\") AS \"shape\",ST_SRID(\"shape\") AS \"shape.STSrid\"");

        dt.GetSelectColumns(cmd, "public", "shapes");
        Assert.That(cmd.CommandText, Does.Contain("ST_SRID"), "spatial columns must carry their SRID");
        Assert.That(cmd.CommandText, Does.Contain(".STSrid"), "the companion must use the shared column name");
    }

    [Test]
    public void GetSelectColumns_MySQL_EmitsSridCompanionForSpatial()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("ST_AsText(`shape`) AS `shape`,ST_SRID(`shape`) AS `shape.STSrid`");

        dt.GetSelectColumns(cmd, "testdb", "shapes");
        Assert.That(cmd.CommandText, Does.Contain("ST_SRID"), "spatial columns must carry their SRID");
        Assert.That(cmd.CommandText, Does.Contain(".STSrid"), "the companion must use the shared column name");
    }

    [Test]
    public void GetSelectColumns_SqlServer_HandlesHierarchyId()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[OrgNode].ToString() AS [OrgNode],[Id]");

        var result = dt.GetSelectColumns(cmd, "dbo", "OrgChart");
        Assert.That(result, Does.Contain("OrgNode"));
        // Verify the query contains the HIERARCHYID case
        Assert.That(cmd.CommandText, Does.Contain("'HIERARCHYID'"));
    }

    [Test]
    public void GetSelectColumns_SqlServer_HierarchyIdQuery_UsesToString()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[Id]");

        dt.GetSelectColumns(cmd, "dbo", "TestTable");
        // Verify the HIERARCHYID branch uses .ToString()
        Assert.That(cmd.CommandText, Does.Contain("HIERARCHYID"));
        Assert.That(cmd.CommandText, Does.Contain(".ToString()"));
    }

    [Test]
    public void GetSelectColumns_PostgreSQL_HandlesArrayColumns()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("ARRAY_TO_STRING(\"tags\", '*,*', '*NULL_VALUE_REPRESENTATION*') AS \"tags\"");

        var result = dt.GetSelectColumns(cmd, "public", "posts");
        Assert.That(result, Does.Contain("ARRAY_TO_STRING"));
    }

    #endregion

    #region GetTableDataJson Tests

    [Test]
    public void GetTableDataJson_SqlServer_UsesForJsonAuto()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[{\"Id\":1}]");

        dt.GetTableDataJson(cmd, "[Id]", "dbo", "Users", "[Id]", null);
        Assert.That(cmd.CommandText, Does.Contain("FOR JSON AUTO"));
    }

    [Test]
    public void GetTableDataJson_PostgreSQL_UsesJsonAgg()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[{\"id\":1}]");

        dt.GetTableDataJson(cmd, "\"id\"", "public", "users", "\"id\"", null);
        Assert.That(cmd.CommandText, Does.Contain("JSON_AGG"));
    }

    [Test]
    public void GetTableDataJson_MySQL_UsesJsonArrayAgg()
    {
        var dt = new global::DataTongs.DataTongs(Platform.MySQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[{\"id\":1}]");

        dt.GetTableDataJson(cmd, "`id`", "testdb", "users", "`id`", null);
        Assert.That(cmd.CommandText, Does.Contain("JSON_ARRAYAGG"));
    }

    [Test]
    public void GetTableDataJson_SqlServer_WithFilter_AddsWhereClause()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[]");

        dt.GetTableDataJson(cmd, "[Id]", "dbo", "Users", "[Id]", "Active = 1");
        Assert.That(cmd.CommandText, Does.Contain("WHERE Active = 1"));
    }

    [Test]
    public void GetTableDataJson_PostgreSQL_WithFilter_AddsWhereClause()
    {
        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[]");

        dt.GetTableDataJson(cmd, "\"id\"", "public", "users", "\"id\"", "active = true");
        Assert.That(cmd.CommandText, Does.Contain("WHERE active = true"));
    }

    [Test]
    public void GetTableDataJson_NullResult_ReturnsEmpty()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns(null);

        var result = dt.GetTableDataJson(cmd, "[Id]", "dbo", "Users", "[Id]", null);
        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public void GetTableDataJson_NoFilter_NoWhereClause()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[{\"Id\":1}]");

        dt.GetTableDataJson(cmd, "[Id]", "dbo", "Users", "[Id]", "");
        Assert.That(cmd.CommandText, Does.Not.Contain("WHERE"));
    }

    #endregion

    #region FormatJsonResult Tests

    [Test]
    public void FormatJsonResult_EmptyString_ReturnsEmpty()
    {
        Assert.That(global::DataTongs.DataTongs.FormatJsonResult(""), Is.EqualTo(""));
    }

    [Test]
    public void FormatJsonResult_Whitespace_ReturnsEmpty()
    {
        Assert.That(global::DataTongs.DataTongs.FormatJsonResult("   "), Is.EqualTo(""));
    }

    [Test]
    public void FormatJsonResult_SingleObject_FormatsWithLineBreaks()
    {
        var result = global::DataTongs.DataTongs.FormatJsonResult("[{\"id\":1}]");
        Assert.That(result, Is.EqualTo("[\r\n{\"id\":1}\r\n]"));
    }

    [Test]
    public void FormatJsonResult_MultipleObjects_FormatsWithLineBreaks()
    {
        var result = global::DataTongs.DataTongs.FormatJsonResult("[{\"id\":1},{\"id\":2}]");
        Assert.That(result, Is.EqualTo("[\r\n{\"id\":1},\r\n{\"id\":2}\r\n]"));
    }

    [Test]
    public void FormatJsonResult_PostgreSqlSpacing_InsertsLineBreaks()
    {
        // PostgreSQL JSON_AGG produces "}, {" with spaces
        var result = global::DataTongs.DataTongs.FormatJsonResult("[{\"a\":1}, {\"b\":2}]");
        Assert.That(result, Is.EqualTo("[\r\n{\"a\":1},\r\n{\"b\":2}\r\n]"));
    }

    #endregion

    #region CountRows Tests

    [Test]
    public void CountRows_FormattedMultiRow_ReturnsActualCount()
    {
        // FormatJsonResult rewrites row separators to "},\r\n{"; the old Split("},{")
        // heuristic never matched that, reporting 1 for any multi-row table.
        var formatted = global::DataTongs.DataTongs.FormatJsonResult(
            "[{\"id\":1},{\"id\":2},{\"id\":3},{\"id\":4},{\"id\":5}]");
        Assert.That(global::DataTongs.DataTongs.CountRows(formatted), Is.EqualTo(5));
    }

    [Test]
    public void CountRows_CompactMultiRow_ReturnsActualCount()
    {
        Assert.That(global::DataTongs.DataTongs.CountRows("[{\"id\":1},{\"id\":2},{\"id\":3}]"), Is.EqualTo(3));
    }

    [Test]
    public void CountRows_SingleRow_Returns1()
    {
        Assert.That(global::DataTongs.DataTongs.CountRows("[{\"id\":1}]"), Is.EqualTo(1));
    }

    [Test]
    public void CountRows_EmptyArray_ReturnsZero()
    {
        Assert.That(global::DataTongs.DataTongs.CountRows("[]"), Is.EqualTo(0));
    }

    #endregion

    #region CastData Tests

    [Test]
    public void CastData_NoSourceDatabase_ThrowsException()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Source:Database"] = "" })
            .Build();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);

            var ex = Assert.Throws<Exception>(() => dt.CastData());
            Assert.That(ex.Message, Does.Contain("Source database is required"));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void CastData_NullSourceDatabase_ThrowsException()
    {
        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>())
            .Build();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);

            var ex = Assert.Throws<Exception>(() => dt.CastData());
            Assert.That(ex.Message, Does.Contain("Source database is required"));

            FactoryContainer.Clear();
        }
    }

    [Test]
    public void CastData_NoTables_CompletesSuccessfully()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("DataTongs completed")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastData_TableDoesNotExist_LogsErrorAndContinues()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["Tables:0:Name"] = "nonexistent"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);
        command.ExecuteScalar().Returns(false); // Table does not exist

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            progressLog.Received().Error(Arg.Is<string>(s => s.Contains("does not exist")));
            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("DataTongs completed")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastData_NoKeyColumns_LogsErrorAndContinues()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["Tables:0:Name"] = "dbo.Users"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        var callCount = 0;
        command.ExecuteScalar().Returns(_ =>
        {
            // The source-version probe (SS-025 encoding choice) is answered by text so it does not shift the sequence.
            if (command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true) return "16.0.1000.6";
            if (command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true) return 160;
            callCount++;
            if (callCount == 1) return (object)true; // TableExists
            return null; // GetKeyColumns returns null
        });

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            progressLog.Received().Error(Arg.Is<string>(s => s.Contains("No match columns found")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastData_WritesContentFile_WhenOutputContentsEnabled()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();
        var fileWrapper = Substitute.For<IFile>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["ShouldCast:OutputContentFiles"] = "true",
            ["ShouldCast:OutputScripts"] = "false",
            ["ContentPath"] = "./content",
            ["Tables:0:Name"] = "dbo.Users",
            ["Tables:0:KeyColumns"] = "[Id]"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        var callCount = 0;
        command.ExecuteScalar().Returns(_ =>
        {
            // The source-version probe (SS-025 encoding choice) is answered by text so it does not shift the sequence.
            if (command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true) return "16.0.1000.6";
            if (command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true) return 160;
            callCount++;
            return callCount switch
            {
                1 => (object)true,               // TableExists
                2 => "[Id],[Name]",               // GetSelectColumns
                3 => "[{\"Id\":1,\"Name\":\"Test\"}]", // GetTableDataJson
                _ => null
            };
        });

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            FactoryContainer.Register(fileWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.Contains("dbo.Users.tabledata")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastData_WritesMergeScript_WhenOutputScriptsEnabled()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();
        var fileWrapper = Substitute.For<IFile>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["ShouldCast:OutputContentFiles"] = "false",
            ["ShouldCast:OutputScripts"] = "true",
            ["ScriptPath"] = "./scripts",
            ["Tables:0:Name"] = "dbo.Users",
            ["Tables:0:KeyColumns"] = "[Id]"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        var callCount = 0;
        command.ExecuteScalar().Returns(_ =>
        {
            // The source-version probe (SS-025 encoding choice) is answered by text so it does not shift the sequence.
            if (command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true) return "16.0.1000.6";
            if (command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true) return 160;
            callCount++;
            return callCount switch
            {
                1 => (object)true,               // TableExists
                2 => "[Id],[Name]",               // GetSelectColumns
                3 => "[{\"Id\":1,\"Name\":\"Test\"}]", // GetTableDataJson
                _ => ""                          // MergeScriptHelper calls
            };
        });

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            FactoryContainer.Register(fileWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.Contains("Populate dbo.Users.sql")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region Platform Logging Tests

    [Test]
    public void CastData_LogsPlatform_SqlServer()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Platform: SqlServer")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastData_LogsPlatform_PostgreSQL()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
            dt.CastData();

            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Platform: PostgreSQL")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region Unsupported Type Exclusion Tests

    [Test]
    public void GetSelectColumns_SqlServer_QueryExcludesUnsupportedTypes()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("[Id],[Name]");

        var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
        dt.GetSelectColumns(cmd, "dbo", "TestTable");

        Assert.That(cmd.CommandText, Does.Contain("sql_variant"));
        Assert.That(cmd.CommandText, Does.Contain("rowversion"));
        Assert.That(cmd.CommandText, Does.Contain("'timestamp'"));
        Assert.That(cmd.CommandText, Does.Contain("NOT IN"));
    }

    [Test]
    public void GetSelectColumns_PostgreSQL_QueryExcludesUnsupportedTypes()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("\"id\",\"name\"");

        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        dt.GetSelectColumns(cmd, "public", "test_table");

        Assert.That(cmd.CommandText, Does.Contain("tsvector"));
        Assert.That(cmd.CommandText, Does.Contain("tsquery"));
        Assert.That(cmd.CommandText, Does.Contain("money"));
        Assert.That(cmd.CommandText, Does.Contain("box"));
        Assert.That(cmd.CommandText, Does.Contain("circle"));
        Assert.That(cmd.CommandText, Does.Contain("line"));
        Assert.That(cmd.CommandText, Does.Contain("lseg"));
        Assert.That(cmd.CommandText, Does.Contain("'path'"));
        Assert.That(cmd.CommandText, Does.Contain("t.typtype = 'c'"));
    }

    [Test]
    public void GetSelectColumns_PostgreSQL_DoesNotExcludePointOrPolygon()
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.ExecuteScalar().Returns("\"id\"");

        var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
        dt.GetSelectColumns(cmd, "public", "test_table");

        // The NOT IN list should not contain 'point' or 'polygon'
        var notInMatch = System.Text.RegularExpressions.Regex.Match(cmd.CommandText, @"NOT IN \([^)]+\)");
        Assert.That(notInMatch.Success, Is.True);
        Assert.That(notInMatch.Value, Does.Not.Contain("'point'"));
        Assert.That(notInMatch.Value, Does.Not.Contain("'polygon'"));
    }

    [Test]
    public void LogUnsupportedColumns_SqlServer_LogsWarnings()
    {
        var progressLog = Substitute.For<ILog>();
        var cmd = Substitute.For<IDbCommand>();
        var reader = Substitute.For<IDataReader>();

        var currentIndex = -1;
        reader.Read().Returns(ci =>
        {
            currentIndex++;
            return currentIndex < 2;
        });
        reader.GetString(0).Returns(ci => currentIndex == 0 ? "VariantCol" : "VersionCol");
        reader.GetString(1).Returns(ci => currentIndex == 0 ? "sql_variant" : "rowversion");
        cmd.ExecuteReader().Returns(reader);

        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.LogUnsupportedColumns(cmd, Platform.SqlServer, "dbo", "TestTable");

            progressLog.Received().Warn(Arg.Is<string>(s =>
                s.Contains("dbo.TestTable.VariantCol") && s.Contains("sql_variant") && s.Contains("not supported")));
            progressLog.Received().Warn(Arg.Is<string>(s =>
                s.Contains("dbo.TestTable.VersionCol") && s.Contains("rowversion") && s.Contains("not supported")));

            LogFactory.Clear();
        }
    }

    [Test]
    public void LogUnsupportedColumns_PostgreSQL_LogsWarnings()
    {
        var progressLog = Substitute.For<ILog>();
        var cmd = Substitute.For<IDbCommand>();
        var reader = Substitute.For<IDataReader>();

        var currentIndex = -1;
        reader.Read().Returns(ci =>
        {
            currentIndex++;
            return currentIndex < 1;
        });
        reader.GetString(0).Returns("search_vec");
        reader.GetString(1).Returns("tsvector");
        cmd.ExecuteReader().Returns(reader);

        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.PostgreSQL);
            dt.LogUnsupportedColumns(cmd, Platform.PostgreSQL, "public", "documents");

            progressLog.Received().Warn(Arg.Is<string>(s =>
                s.Contains("public.documents.search_vec") && s.Contains("tsvector") && s.Contains("not supported")));

            LogFactory.Clear();
        }
    }

    [Test]
    public void LogUnsupportedColumns_MySQL_DoesNotLogAnything()
    {
        var progressLog = Substitute.For<ILog>();
        var cmd = Substitute.For<IDbCommand>();

        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.MySQL);
            dt.LogUnsupportedColumns(cmd, Platform.MySQL, "testdb", "test_table");

            progressLog.DidNotReceive().Warn(Arg.Any<string>());

            LogFactory.Clear();
        }
    }

    [Test]
    public void LogUnsupportedColumns_SqlServer_NoUnsupportedColumns_NoWarnings()
    {
        var progressLog = Substitute.For<ILog>();
        var cmd = Substitute.For<IDbCommand>();
        var reader = Substitute.For<IDataReader>();

        reader.Read().Returns(false); // No rows
        cmd.ExecuteReader().Returns(reader);

        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.LogUnsupportedColumns(cmd, Platform.SqlServer, "dbo", "TestTable");

            progressLog.DidNotReceive().Warn(Arg.Any<string>());

            LogFactory.Clear();
        }
    }

    #endregion

    #region TableConfig Tests

    [Test]
    public void TableConfig_DefaultValues()
    {
        var config = new global::DataTongs.DataTongs.TableConfig();
        Assert.Multiple(() =>
        {
            Assert.That(config.TableName, Is.EqualTo(""));
            Assert.That(config.KeyColumns, Is.EqualTo(""));
            Assert.That(config.SelectColumns, Is.EqualTo(""));
            Assert.That(config.Filter, Is.EqualTo(""));
            Assert.That(config.MergeType, Is.EqualTo(""));
        });
    }

    #endregion

    #region CastData — MySQL with SelectColumns (configSelectColumns branch)

    [Test]
    public void CastData_MySQL_WithSelectColumns_UsesConfiguredColumns()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();
        var fileWrapper = Substitute.For<IFile>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["ShouldCast:OutputContentFiles"] = "true",
            ["ShouldCast:OutputScripts"] = "false",
            ["ContentPath"] = "./content",
            ["Tables:0:Name"] = "users",
            ["Tables:0:KeyColumns"] = "`id`",
            ["Tables:0:SelectColumns"] = "`id`,`name`,`email`"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        var callCount = 0;
        command.ExecuteScalar().Returns(_ =>
        {
            // The source-version probe (SS-025 encoding choice) is answered by text so it does not shift the sequence.
            if (command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true) return "16.0.1000.6";
            if (command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true) return 160;
            // Data delivery detects the server version (TargetVersionDetector) and probes JSON_ARRAYAGG support,
            // both via SELECT VERSION() — return a modern MySQL version so the JSON_ARRAYAGG path is taken.
            if (command.CommandText?.Contains("VERSION()") == true) return "8.0.36";
            callCount++;
            return callCount switch
            {
                1 => (object)1L, // TableExists (MySQL)
                2 => "[{\"id\":1,\"name\":\"Test\",\"email\":\"t@t.com\"}]", // GetTableDataJsonMySql
                _ => null
            };
        });

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            FactoryContainer.Register(fileWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.MySQL);
            dt.CastData();

            // configSelectColumns branch: uses configured columns, builds JSON_OBJECT with varchar defaults
            Assert.That(command.CommandText, Does.Contain("JSON_ARRAYAGG"));
            Assert.That(command.CommandText, Does.Contain("JSON_OBJECT"));
            Assert.That(command.CommandText, Does.Contain("`id`"));
            Assert.That(command.CommandText, Does.Contain("`name`"));
            Assert.That(command.CommandText, Does.Contain("`email`"));

            fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.Contains("users.tabledata")),
                Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region CastData — Invalid Key Columns

    [Test]
    public void CastData_InvalidKeyColumns_LogsErrorAndContinues()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["Tables:0:Name"] = "dbo.Users",
            ["Tables:0:KeyColumns"] = "[\"Id\",\"Name\"]" // JSON array format — invalid
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);
        command.ExecuteScalar().Returns(true); // TableExists

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            progressLog.Received().Error(Arg.Is<string>(s => s.Contains("Invalid KeyColumns")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region CastData — Empty Data Result

    [Test]
    public void CastData_EmptyDataResult_WritesEmptyContentFile()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();
        var fileWrapper = Substitute.For<IFile>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["ShouldCast:OutputContentFiles"] = "true",
            ["ShouldCast:OutputScripts"] = "false",
            ["ContentPath"] = "./content",
            ["Tables:0:Name"] = "dbo.Users",
            ["Tables:0:KeyColumns"] = "[Id]"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        var callCount = 0;
        command.ExecuteScalar().Returns(_ =>
        {
            // The source-version probe (SS-025 encoding choice) is answered by text so it does not shift the sequence.
            if (command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true) return "16.0.1000.6";
            if (command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true) return 160;
            callCount++;
            return callCount switch
            {
                1 => (object)true, // TableExists
                2 => "[Id],[Name]", // GetSelectColumns
                3 => (object)null, // GetTableDataJson — no rows
                _ => null
            };
        });

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            FactoryContainer.Register(fileWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("No rows found")));
            fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.Contains("dbo.Users.tabledata")),
                Arg.Is<string>(s => s == "[]"));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region CastData — Error Handling

    [Test]
    public void CastData_TableProcessingError_LogsErrorAndContinues()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["Tables:0:Name"] = "dbo.Users",
            ["Tables:0:KeyColumns"] = "[Id]"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        var callCount = 0;
        command.ExecuteScalar().Returns(_ =>
        {
            // The source-version probe (SS-025 encoding choice) is answered by text so it does not shift the sequence.
            if (command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true) return "16.0.1000.6";
            if (command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true) return 160;
            callCount++;
            if (callCount == 1) return (object)true; // TableExists
            throw new Exception("Simulated database error");
        });

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.SqlServer);
            dt.CastData();

            progressLog.Received().Error(Arg.Is<string>(s =>
                s.Contains("Error processing table") && s.Contains("Simulated database error")));
            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Errors: 1")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region CastData — MySQL Null Data Result

    [Test]
    public void CastData_MySQL_NullDataResult_WritesEmptyArray()
    {
        var progressLog = Substitute.For<ILog>();
        var connectionFactory = Substitute.For<IDbConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var command = Substitute.For<IDbCommand>();
        var dirWrapper = Substitute.For<IDirectory>();
        var fileWrapper = Substitute.For<IFile>();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "user",
            ["Source:Password"] = "pass",
            ["ShouldCast:OutputContentFiles"] = "true",
            ["ShouldCast:OutputScripts"] = "false",
            ["ContentPath"] = "./content",
            ["Tables:0:Name"] = "users",
            ["Tables:0:KeyColumns"] = "`id`"
        }).Build();

        connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        connection.CreateCommand().Returns(command);

        var reader = Substitute.For<IDataReader>();
        var readerIndex = -1;
        reader.Read().Returns(_ => { readerIndex++; return readerIndex < 1; });
        reader.GetString(0).Returns("id");
        reader.GetString(1).Returns("int");
        command.ExecuteReader().Returns(reader);

        var callCount = 0;
        command.ExecuteScalar().Returns(_ =>
        {
            // The source-version probe (SS-025 encoding choice) is answered by text so it does not shift the sequence.
            if (command.CommandText?.Contains("CONVERT(varchar(50), SERVERPROPERTY") == true) return "16.0.1000.6";
            if (command.CommandText?.Contains("FROM sys.databases WHERE name = N'") == true) return 160;
            // Data delivery detects the server version and probes JSON_ARRAYAGG support, both via SELECT VERSION().
            if (command.CommandText?.Contains("VERSION()") == true) return "8.0.36";
            callCount++;
            return callCount switch
            {
                1 => (object)1L,  // TableExists (MySQL)
                2 => (object)null, // GetTableDataJsonMySql — null result
                _ => null
            };
        });

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register<IDbConnectionFactory>(connectionFactory);
            FactoryContainer.Register(dirWrapper);
            FactoryContainer.Register(fileWrapper);
            LogFactory.Register("ProgressLog", progressLog);

            var dt = new global::DataTongs.DataTongs(Platform.MySQL);
            dt.CastData();

            // MySQL GetTableDataJsonMySql returns "[]" for null, which triggers the empty data path
            progressLog.Received().Info(Arg.Is<string>(s => s.Contains("No rows found")));
            fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.Contains("users.tabledata")),
                Arg.Is<string>(s => s == "[]"));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region XML delivery value fragments (B1 slice 3)

    private static string Fragment(string name, string dataType) =>
        global::DataTongs.DataTongs.BuildSqlServerXmlValueFragment(
            new global::DataTongs.DataTongs.ColumnInfo { Name = name, DataType = dataType });

    [Test]
    public void XmlFragment_NormalColumn_ConvertsToText()
    {
        Assert.That(Fragment("Amount", "decimal"), Is.EqualTo("('Amount', CONVERT(NVARCHAR(MAX), t.[Amount]))"));
    }

    [Test]
    public void XmlFragment_Datetime_UsesIso8601Style126()
    {
        Assert.That(Fragment("When", "datetime2"), Is.EqualTo("('When', CONVERT(NVARCHAR(MAX), t.[When], 126))"));
    }

    [Test]
    public void XmlFragment_Binary_Base64Encodes()
    {
        Assert.That(Fragment("Blob", "varbinary"),
            Does.Contain("xs:base64Binary(sql:column(\"t.[Blob]\"))"));
    }

    [Test]
    public void XmlFragment_Geometry_EmitsWktPlusSridCompanion()
    {
        var frag = Fragment("Shape", "geometry");
        Assert.That(frag, Does.Contain("('Shape', t.[Shape].STAsText())"));
        Assert.That(frag, Does.Contain("('Shape.STSrid', CONVERT(NVARCHAR(MAX), t.[Shape].STSrid))"));
    }

    [Test]
    public void XmlFragment_EscapesNameAndIdentifierDelimiters()
    {
        // A quote in the name is doubled for the SQL string literal; a bracket is doubled for the identifier.
        Assert.That(Fragment("O'Brien]col", "int"),
            Is.EqualTo("('O''Brien]col', CONVERT(NVARCHAR(MAX), t.[O'Brien]]col]))"));
    }

    [Test]
    public void CountXmlRows_CountsRowElements_NotTheRootOrColumns()
    {
        const string xml = "<rows><row><c n=\"id\">1</c></row><row><c n=\"id\">2</c></row></rows>";
        Assert.That(global::DataTongs.DataTongs.CountXmlRows(xml), Is.EqualTo(2));
        Assert.That(global::DataTongs.DataTongs.CountXmlRows("<rows></rows>"), Is.EqualTo(0));
        Assert.That(global::DataTongs.DataTongs.CountXmlRows(""), Is.EqualTo(0));
    }

    #endregion
}
