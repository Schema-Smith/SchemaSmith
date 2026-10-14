// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using log4net;
using NSubstitute;

namespace SchemaTongs.IntegrationTests.SqlServer;

[Category("SqlServer")]
public class SchemaTongsTests
{
    private string _integrationDb = "";
    private string _connectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master", config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _integrationDb = GenerateUniqueDBName(config["Source:database"] ?? "TongsTest");

        CreateTestDatabases();
    }

    [Test]
    public void ShouldCastTables()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Tables"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Tables", "Test.TestTable.json"))), Arg.Any<string>());

            config["ShouldCast:Tables"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastViews()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Views"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Views", "Test.TestView.sql"))), Arg.Any<string>());

            config["ShouldCast:Views"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }


    [Test]
    public void ShouldCastStoredProcedures()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Procedures"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Procedures", "Test.TestProcedure.sql"))), Arg.Any<string>());

            config["ShouldCast:Procedures"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastUserDefinedFunctions()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Functions"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Functions", "Test.TestFunction.sql"))), Arg.Any<string>());

            config["ShouldCast:Functions"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastUserDefinedTypes()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:UserDefinedTypes"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(8).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("DataTypes", "Test.Flag.sql"))), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("DataTypes", "Test.TestTableType.sql"))), Arg.Any<string>());

            config["ShouldCast:UserDefinedTypes"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastSchemas()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Schemas"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Schemas", "Test.sql"))), Arg.Any<string>());

            config["ShouldCast:Schemas"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastTableTriggers()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:TableTriggers"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("Triggers", "Test.TestTable.TestTrigger.sql"))), Arg.Any<string>());

            config["ShouldCast:TableTriggers"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastCatalogs()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:Catalogs"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("FullTextCatalogs", "FT_Catalog.sql"))), Arg.Any<string>());

            config["ShouldCast:Catalogs"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastStopLists()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:StopLists"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("FullTextStopLists", "SL_Test.sql"))), Arg.Any<string>());

            config["ShouldCast:StopLists"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastDDLTriggers()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:DDLTriggers"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("DDLTriggers", "safety.sql"))), Arg.Any<string>());

            config["ShouldCast:DDLTriggers"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ShouldCastXMLSchemaCollections()
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            config["ShouldCast:XMLSchemaCollections"] = "true";

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            file.Received(7).WriteAllText(Arg.Any<string>(), Arg.Any<string>());
            file.Received(4).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(".schema")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("product.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase("template.json")), Arg.Any<string>());
            file.Received(1).WriteAllText(Arg.Is<string>(s => s.EndsWithIgnoringCase(Path.Combine("XMLSchemaCollections", "dbo.ManuInstructionsSchemaCollection.sql"))), Arg.Any<string>());

            config["ShouldCast:XMLSchemaCollections"] = "false";
            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    // --- Content-asserting baseline tests ---

    private Dictionary<string, string> RunCastWithCapture(Action<IConfigurationRoot> configureFlags)
    {
        var errorLog = Substitute.For<ILog>();
        var progressLog = Substitute.For<ILog>();
        var environment = Substitute.For<IEnvironment>();
        var file = Substitute.For<IFile>();
        var directory = Substitute.For<IDirectory>();

        var capturedContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => capturedContent[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1));

        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            FactoryContainer.Register(environment);
            FactoryContainer.Register(file);
            FactoryContainer.Register(directory);
            var config = SetupConfig();
            configureFlags(config);

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            FactoryContainer.Clear();
            LogFactory.Clear();
        }

        return capturedContent;
    }

    [Test]
    public void ShouldCastSchemaContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:Schemas"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("Schemas", "Test.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = N'Test')\r\n" +
            "EXEC sys.sp_executesql N'CREATE SCHEMA [Test]'\r\n"));
    }

    [Test]
    public void ShouldCastAliasTypeContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:UserDefinedTypes"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("DataTypes", "Test.Flag.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "IF NOT EXISTS (SELECT * FROM sys.types st JOIN sys.schemas ss ON st.schema_id = ss.schema_id WHERE st.name = N'Flag' AND ss.name = N'Test')\r\n" +
            "CREATE TYPE [Test].[Flag] FROM [bit] NOT NULL"));
    }

    [Test]
    public void ShouldCastTableTypeContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:UserDefinedTypes"] = "true");

        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("DataTypes", "Test.TestTableType.sql"))).Value;

        // Verify structural elements (no SMO baseline to match exactly)
        Assert.That(content, Does.Contain("CREATE TYPE [Test].[TestTableType] AS TABLE"));
        Assert.That(content, Does.Contain("[Id]"));
        Assert.That(content, Does.Contain("[Name]"));
        Assert.That(content, Does.Contain("[Amount]"));
        Assert.That(content, Does.Contain("NOT NULL"));
        Assert.That(content, Does.Contain("PRIMARY KEY"));
    }

    [Test]
    public void ShouldCastFullTextCatalogContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:Catalogs"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("FullTextCatalogs", "FT_Catalog.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "IF NOT EXISTS (SELECT * FROM sys.fulltext_catalogs ftc WHERE ftc.name = N'FT_Catalog')\r\n" +
            "CREATE FULLTEXT CATALOG [FT_Catalog] WITH ACCENT_SENSITIVITY = ON"));
    }

    [Test]
    public void ShouldCastFullTextStopListContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:StopLists"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("FullTextStopLists", "SL_Test.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "IF NOT EXISTS (SELECT * FROM sys.fulltext_stoplists ftsl WHERE ftsl.name = N'SL_Test')\r\n" +
            "BEGIN\r\n" +
            "CREATE FULLTEXT STOPLIST [SL_Test]\r\n" +
            ";\r\n" +
            "ALTER FULLTEXT STOPLIST [SL_Test] ADD '$' LANGUAGE 'Neutral';\r\n" +
            "END\r\n"));
    }

    [Test]
    public void ShouldCastStoredProcedureContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:Procedures"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("Procedures", "Test.TestProcedure.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "SET ANSI_NULLS ON\r\n" +
            "SET QUOTED_IDENTIFIER ON\r\n" +
            "GO\r\n\r\n" +
            "CREATE OR ALTER PROCEDURE [Test].[TestProcedure] @param INT\r\n" +
            "AS\r\n\r\n" +
            "BEGIN\r\n" +
            "    SELECT @param;\r\n" +
            "END;\r\n\r\n" +
            "GO\r\n"));
    }

    [Test]
    public void ShouldCastFunctionContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:Functions"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("Functions", "Test.TestFunction.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "SET ANSI_NULLS ON\r\n" +
            "SET QUOTED_IDENTIFIER ON\r\n" +
            "GO\r\n\r\n" +
            "CREATE OR ALTER FUNCTION [Test].[TestFunction](@param INT) RETURNS INT\r\n" +
            "AS\r\n\r\n" +
            "BEGIN\r\n" +
            "    RETURN @param;\r\n" +
            "END;\r\n\r\n" +
            "GO\r\n"));
    }

    [Test]
    public void ShouldCastViewContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:Views"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("Views", "Test.TestView.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "SET ANSI_NULLS ON\r\n" +
            "SET QUOTED_IDENTIFIER ON\r\n" +
            "GO\r\n\r\n" +
            "CREATE OR ALTER VIEW [Test].[TestView] \r\n" +
            "AS \r\n\r\n" +
            "SELECT * \r\n" +
            "  FROM Test.TestTable\r\n\r\n" +
            "GO\r\n"));
    }

    [Test]
    public void ShouldCastTableTriggerContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:TableTriggers"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("Triggers", "Test.TestTable.TestTrigger.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "SET ANSI_NULLS ON\r\n" +
            "SET QUOTED_IDENTIFIER ON\r\n" +
            "GO\r\n\r\n" +
            "CREATE OR ALTER TRIGGER [Test].[TestTrigger] ON [Test].[TestTable] AFTER INSERT\r\n" +
            "AS\r\n\r\n" +
            "BEGIN\r\n" +
            "    DECLARE @id INT;\r\n" +
            "    SELECT @id = Column1 FROM inserted;\r\n" +
            "    INSERT INTO Test.TestLog (Msg) VALUES ('Trigger fired for ID: ' + CAST(@id AS VARCHAR(10)));\r\n" +
            "END;\r\n\r\n" +
            "GO\r\n"));
    }

    [Test]
    public void ShouldCastDDLTriggerContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:DDLTriggers"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("DDLTriggers", "safety.sql"))).Value;
        Assert.That(content, Is.EqualTo(
            "SET ANSI_NULLS ON\r\n" +
            "SET QUOTED_IDENTIFIER ON\r\n" +
            "GO\r\n\r\n" +
            "CREATE OR ALTER TRIGGER [safety] ON DATABASE FOR DROP_TABLE\r\n" +
            "AS   \r\n" +
            "   \r\n" +
            "INSERT INTO Test.TestLog (Msg) VALUES ('Dropping Tables is bad!');\r\n\r\n" +
            "GO\r\n"));
    }

    [Test]
    public void ShouldCastXmlSchemaCollectionContent()
    {
        var captured = RunCastWithCapture(config => config["ShouldCast:XMLSchemaCollections"] = "true");
        var content = captured.First(kvp => kvp.Key.EndsWithIgnoringCase(Path.Combine("XMLSchemaCollections", "dbo.ManuInstructionsSchemaCollection.sql"))).Value;
        var expected =
            "IF NOT EXISTS (SELECT * FROM sys.xml_schema_collections c, sys.schemas s WHERE c.schema_id = s.schema_id AND (quotename(s.name) + '.' + quotename(c.name)) = N'[dbo].[ManuInstructionsSchemaCollection]')\r\n" +
            "CREATE XML SCHEMA COLLECTION [dbo].[ManuInstructionsSchemaCollection] AS N'\r\n" +
            "<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:t=\"https://schemas.microsoft.com/sqlserver/2004/07/adventure-works/ProductModelManuInstructions\" targetNamespace=\"https://schemas.microsoft.com/sqlserver/2004/07/adventure-works/ProductModelManuInstructions\" elementFormDefault=\"qualified\">\r\n" +
            "  <xsd:element name=\"root\">\r\n" +
            "    <xsd:complexType mixed=\"true\">\r\n" +
            "      <xsd:complexContent mixed=\"true\">\r\n" +
            "        <xsd:restriction base=\"xsd:anyType\">\r\n" +
            "          <xsd:sequence>\r\n" +
            "            <xsd:element name=\"Location\" maxOccurs=\"unbounded\">\r\n" +
            "              <xsd:complexType mixed=\"true\">\r\n" +
            "                <xsd:complexContent mixed=\"true\">\r\n" +
            "                  <xsd:restriction base=\"xsd:anyType\">\r\n" +
            "                    <xsd:sequence>\r\n" +
            "                      <xsd:element name=\"step\" type=\"t:StepType\" maxOccurs=\"unbounded\" />\r\n" +
            "                    </xsd:sequence>\r\n" +
            "                    <xsd:attribute name=\"LocationID\" type=\"xsd:integer\" use=\"required\" />\r\n" +
            "                    <xsd:attribute name=\"SetupHours\" type=\"xsd:decimal\" />\r\n" +
            "                    <xsd:attribute name=\"MachineHours\" type=\"xsd:decimal\" />\r\n" +
            "                    <xsd:attribute name=\"LaborHours\" type=\"xsd:decimal\" />\r\n" +
            "                    <xsd:attribute name=\"LotSize\" type=\"xsd:decimal\" />\r\n" +
            "                  </xsd:restriction>\r\n" +
            "                </xsd:complexContent>\r\n" +
            "              </xsd:complexType>\r\n" +
            "            </xsd:element>\r\n" +
            "          </xsd:sequence>\r\n" +
            "        </xsd:restriction>\r\n" +
            "      </xsd:complexContent>\r\n" +
            "    </xsd:complexType>\r\n" +
            "  </xsd:element>\r\n" +
            "  <xsd:complexType name=\"StepType\" mixed=\"true\">\r\n" +
            "    <xsd:complexContent mixed=\"true\">\r\n" +
            "      <xsd:restriction base=\"xsd:anyType\">\r\n" +
            "        <xsd:choice minOccurs=\"0\" maxOccurs=\"unbounded\">\r\n" +
            "          <xsd:element name=\"tool\" type=\"xsd:string\" />\r\n" +
            "          <xsd:element name=\"material\" type=\"xsd:string\" />\r\n" +
            "          <xsd:element name=\"blueprint\" type=\"xsd:string\" />\r\n" +
            "          <xsd:element name=\"specs\" type=\"xsd:string\" />\r\n" +
            "          <xsd:element name=\"diag\" type=\"xsd:string\" />\r\n" +
            "        </xsd:choice>\r\n" +
            "      </xsd:restriction>\r\n" +
            "    </xsd:complexContent>\r\n" +
            "  </xsd:complexType>\r\n" +
            "</xsd:schema>\r\n" +
            "'";
        Assert.That(content, Is.EqualTo(expected));
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private IConfigurationRoot SetupConfig()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);

        // Map SqlServer-specific config to Source:* keys used by SchemaTongs.GetConnection
        config["Source:Server"] = config["SqlServer:Server"] ?? "127.0.0.1";
        config["Source:Port"] = config["SqlServer:Port"];
        config["Source:User"] = config["SqlServer:User"];
        config["Source:Password"] = config["SqlServer:Password"];
        config["Source:database"] = _integrationDb;
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        foreach (var prop in connProps)
            config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;

        config["ShouldCast:Tables"] = "false";
        config["ShouldCast:Schemas"] = "false";
        config["ShouldCast:UserDefinedTypes"] = "false";
        config["ShouldCast:Functions"] = "false";
        config["ShouldCast:Views"] = "false";
        config["ShouldCast:Procedures"] = "false";
        config["ShouldCast:TableTriggers"] = "false";
        config["ShouldCast:Catalogs"] = "false";
        config["ShouldCast:StopLists"] = "false";
        config["ShouldCast:DDLTriggers"] = "false";
        config["ShouldCast:XMLSchemaCollections"] = "false";
        config["ShouldCast:IndexedViews"] = "false";
        FactoryContainer.Register<IConfigurationRoot>(config);
        return config;
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE [{_integrationDb}] COLLATE {Schema.IntegrationTests.SqlServer.ForeignCollation.For(cmd)};
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);

        cmd.CommandText = @"
CREATE FULLTEXT CATALOG [FT_Catalog]
CREATE FULLTEXT STOPLIST [SL_Test];
ALTER FULLTEXT STOPLIST [SL_Test] ADD '$' LANGUAGE 'Neutral';

EXEC('CREATE SCHEMA [Test]')
CREATE TYPE [Test].[Flag] FROM BIT NOT NULL
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE TYPE [Test].[TestTableType] AS TABLE (
    [Id] INT NOT NULL,
    [Name] NVARCHAR(100) NULL,
    [Amount] DECIMAL(18,2) NOT NULL,
    PRIMARY KEY CLUSTERED ([Id])
)
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE TABLE Test.TestTable (Column1 INT NOT NULL, Column2 VARCHAR(200) NULL, Column3 [Test].[Flag])
CREATE UNIQUE CLUSTERED INDEX UDX_Key ON Test.TestTable ([Column1])
CREATE FULLTEXT INDEX ON Test.TestTable (Column2) KEY INDEX UDX_Key ON [FT_Catalog] WITH CHANGE_TRACKING=AUTO, STOPLIST = [SL_Test];
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE XML SCHEMA COLLECTION ManuInstructionsSchemaCollection AS
N'<?xml version=""1.0"" encoding=""UTF-16""?>
<xsd:schema targetNamespace=""https://schemas.microsoft.com/sqlserver/2004/07/adventure-works/ProductModelManuInstructions""
   xmlns          =""https://schemas.microsoft.com/sqlserver/2004/07/adventure-works/ProductModelManuInstructions""
   elementFormDefault=""qualified""
   attributeFormDefault=""unqualified""
   xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" >

    <xsd:complexType name=""StepType"" mixed=""true"" >
        <xsd:choice  minOccurs=""0"" maxOccurs=""unbounded"" >
            <xsd:element name=""tool"" type=""xsd:string"" />
            <xsd:element name=""material"" type=""xsd:string"" />
            <xsd:element name=""blueprint"" type=""xsd:string"" />
            <xsd:element name=""specs"" type=""xsd:string"" />
            <xsd:element name=""diag"" type=""xsd:string"" />
        </xsd:choice>
    </xsd:complexType>

    <xsd:element  name=""root"">
        <xsd:complexType mixed=""true"">
            <xsd:sequence>
                <xsd:element name=""Location"" minOccurs=""1"" maxOccurs=""unbounded"">
                    <xsd:complexType mixed=""true"">
                        <xsd:sequence>
                            <xsd:element name=""step"" type=""StepType"" minOccurs=""1"" maxOccurs=""unbounded"" />
                        </xsd:sequence>
                        <xsd:attribute name=""LocationID"" type=""xsd:integer"" use=""required""/>
                        <xsd:attribute name=""SetupHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""MachineHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""LaborHours"" type=""xsd:decimal"" use=""optional""/>
                        <xsd:attribute name=""LotSize"" type=""xsd:decimal"" use=""optional""/>
                    </xsd:complexType>
                </xsd:element>
            </xsd:sequence>
        </xsd:complexType>
    </xsd:element>
</xsd:schema>';
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE TRIGGER [safety] ON DATABASE FOR DROP_TABLE\r\nAS   \r\n   INSERT INTO Test.TestLog (Msg) VALUES ('Dropping Tables is bad!');\r\n";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE TRIGGER Test.TestTrigger ON Test.TestTable AFTER INSERT
AS
BEGIN
    DECLARE @id INT;
    SELECT @id = Column1 FROM inserted;
    INSERT INTO Test.TestLog (Msg) VALUES ('Trigger fired for ID: ' + CAST(@id AS VARCHAR(10)));
END;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE VIEW Test.TestView \r\nAS \r\nSELECT * \r\n  FROM Test.TestTable\r\n";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE FUNCTION Test.TestFunction(@param INT) RETURNS INT
AS
BEGIN
    RETURN @param;
END;
";
        cmd.ExecuteNonQuery();

        cmd.CommandText = @"
CREATE PROCEDURE Test.TestProcedure @param INT
AS
BEGIN
    SELECT @param;
END;
";
        cmd.ExecuteNonQuery();

        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace(" - ", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, dbName);
    }
}
