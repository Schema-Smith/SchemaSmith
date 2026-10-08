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

namespace SchemaTongs.UnitTests;

[TestFixture]
public class SchemaTongsTests
{
    private ILog _progressLog;
    private ILog _errorLog;
    private IEnvironment _environment;
    private IDbConnectionFactory _connectionFactory;
    private IDbConnection _connection;
    private IDbCommand _command;
    private IFile _fileWrapper;
    private IDirectory _directoryWrapper;

    private void SetUpMocks()
    {
        _progressLog = Substitute.For<ILog>();
        _errorLog = Substitute.For<ILog>();
        _environment = Substitute.For<IEnvironment>();
        _connectionFactory = Substitute.For<IDbConnectionFactory>();
        _connection = Substitute.For<IDbConnection>();
        _command = Substitute.For<IDbCommand>();
        _fileWrapper = Substitute.For<IFile>();
        _directoryWrapper = Substitute.For<IDirectory>();

        _connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(_connection);
        _connection.CreateCommand().Returns(_command);
        _directoryWrapper.Exists(Arg.Any<string>()).Returns(true);
        _directoryWrapper.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns(Array.Empty<string>());
        _fileWrapper.Exists(Arg.Any<string>()).Returns(false);
    }

    /// <summary>
    /// Stub <paramref name="mockCmd"/>.ExecuteScalar() so that KindleTheForge takes the
    /// skip path on MySQL without touching the DB. Lock-acquire returns 1L (success),
    /// the KindleStamp existence check returns 1L (table present), and the stamp SELECT
    /// returns the current computed stamp so the gate detects "already current" and returns
    /// without executing any kindle DDL. Non-MySQL kindle queries fall through to null,
    /// preserving default NSubstitute behaviour for SQL Server and PostgreSQL tests.
    /// Call this immediately after SetUpMocks() in MySQL-targeted tests that call
    /// CastTemplate() — BEFORE any per-test ExecuteScalar stubs that need to return
    /// query-specific values, so those per-test stubs (which replace this one) are
    /// last-installed and therefore win.
    /// </summary>
    private void StubMySqlKindleGate() => KindleGateTestHelpers.StubMySqlKindleGate(_command);

    private void RegisterConfig(Platform platform, Dictionary<string, string> overrides = null)
    {
        var configValues = new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "testuser",
            ["Source:Password"] = "testpass",
            ["Target:Platform"] = platform.ToString(),
            ["Product:Path"] = Path.GetTempPath(),
            ["Product:Name"] = "TestProduct",
            ["Template:Name"] = "TestTemplate"
        };

        if (overrides != null)
        {
            foreach (var kv in overrides)
                configValues[kv.Key] = kv.Value;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        FactoryContainer.Register<IConfigurationRoot>(config);
        FactoryContainer.Register(_environment);
        FactoryContainer.Register(_fileWrapper);
        FactoryContainer.Register(_directoryWrapper);
        LogFactory.Register("ProgressLog", _progressLog);
        LogFactory.Register("ErrorLog", _errorLog);

        // Product.json must exist and return platform-appropriate JSON
        _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(true);
        _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(
            $"{{\"Name\":\"TestProduct\",\"Platform\":\"{platform}\",\"TemplateOrder\":[\"TestTemplate\"],\"ScriptTokens\":{{}},\"ScriptFolders\":[]}}");
        _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(true);
        _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(
            "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[],\"ScriptTokens\":{}}");
    }

    private void RegisterConnectionFactory(Platform platform)
    {
        // All platform-specific factories resolve via FactoryContainer.ResolveOrCreate<IDbConnectionFactory, T>()
        // So registering IDbConnectionFactory covers all platforms
        FactoryContainer.Register(_connectionFactory);
    }

    #region Common / Constructor Tests

    [Test]
    public void CastTemplate_MissingDatabase_ThrowsException()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["Source:Database"] = null
            });

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.Throws<Exception>(() => tongs.CastTemplate());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_EmptyDatabase_ThrowsException()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["Source:Database"] = ""
            });

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.Throws<Exception>(() => tongs.CastTemplate());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region SQL Server Tests

    [Test]
    public void CastTemplate_SqlServer_AllDisabled_CompletesSuccessfully()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Synonyms"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Summary")));
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Completed Successfully")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_DefaultsAllTrue_KindlesForge()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);
            RegisterConnectionFactory(Platform.SqlServer);

            var emptyReader = Substitute.For<IDataReader>();
            emptyReader.Read().Returns(false);
            _command.StubReaders(emptyReader);

            // Second connection for table JSON extraction
            var connection2 = Substitute.For<IDbConnection>();
            var command2 = Substitute.For<IDbCommand>();
            connection2.CreateCommand().Returns(command2);
            var emptyReader2 = Substitute.For<IDataReader>();
            emptyReader2.Read().Returns(false);
            command2.StubReaders(emptyReader2);

            var connectionCallCount = 0;
            _connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(_ =>
            {
                connectionCallCount++;
                return connectionCallCount <= 1 ? _connection : connection2;
            });

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Kindling The Forge")));
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Table Structures")));
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Completed Successfully")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_SchemasOnly_CastsSchemas()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "true",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: schema list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("TestSchema");
            listReader.GetInt32(1).Returns(5);

            // 2nd reader: extended properties (empty)
            var extReader = Substitute.For<IDataReader>();
            extReader.Read().Returns(false);

            _command.StubReaders(listReader, extReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Schema Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("TestSchema.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_FunctionsOnly_CastsFunctions()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "true",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: function list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("dbo");
            listReader.GetString(1).Returns("MyFunc");

            // 2nd reader: definition from ScriptSqlServerProgrammableObject
            var defReader = Substitute.For<IDataReader>();
            defReader.Read().Returns(true, false);
            defReader.IsDBNull(0).Returns(false);
            defReader.GetString(0).Returns("CREATE FUNCTION dbo.MyFunc() RETURNS INT AS BEGIN RETURN 1 END");
            defReader.GetBoolean(1).Returns(true);
            defReader.GetBoolean(2).Returns(true);

            // 3rd reader: extended properties (empty)
            var extReader = Substitute.For<IDataReader>();
            extReader.Read().Returns(false);

            _command.StubReaders(listReader, defReader, extReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Function Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("dbo.MyFunc.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_ViewsOnly_CastsViews()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "true",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: view list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("dbo");
            listReader.GetString(1).Returns("MyView");

            // 2nd reader: definition from ScriptSqlServerProgrammableObject
            var defReader = Substitute.For<IDataReader>();
            defReader.Read().Returns(true, false);
            defReader.IsDBNull(0).Returns(false);
            defReader.GetString(0).Returns("CREATE VIEW dbo.MyView AS SELECT 1 AS Val");
            defReader.GetBoolean(1).Returns(true);
            defReader.GetBoolean(2).Returns(true);

            // 3rd reader: extended properties (empty)
            var extReader = Substitute.For<IDataReader>();
            extReader.Read().Returns(false);

            _command.StubReaders(listReader, defReader, extReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting View Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("dbo.MyView.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_ProceduresOnly_CastsProcedures()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "true",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: procedure list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("dbo");
            listReader.GetString(1).Returns("MyProc");

            // 2nd reader: definition from ScriptSqlServerProgrammableObject
            var defReader = Substitute.For<IDataReader>();
            defReader.Read().Returns(true, false);
            defReader.IsDBNull(0).Returns(false);
            defReader.GetString(0).Returns("CREATE PROCEDURE dbo.MyProc AS BEGIN SELECT 1 END");
            defReader.GetBoolean(1).Returns(true);
            defReader.GetBoolean(2).Returns(true);

            // 3rd reader: extended properties (empty)
            var extReader = Substitute.For<IDataReader>();
            extReader.Read().Returns(false);

            _command.StubReaders(listReader, defReader, extReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Stored Procedure Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("dbo.MyProc.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_TableTriggersOnly_CastsTriggers()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "true",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: trigger list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("dbo");
            listReader.GetString(1).Returns("MyTable");
            listReader.GetString(2).Returns("MyTrigger");

            // 2nd reader: definition from ScriptSqlServerProgrammableObject
            var defReader = Substitute.For<IDataReader>();
            defReader.Read().Returns(true, false);
            defReader.IsDBNull(0).Returns(false);
            defReader.GetString(0).Returns("CREATE TRIGGER dbo.MyTrigger ON dbo.MyTable AFTER INSERT AS BEGIN RETURN END");
            defReader.GetBoolean(1).Returns(true);
            defReader.GetBoolean(2).Returns(true);

            // 3rd reader: extended properties (empty)
            var extReader = Substitute.For<IDataReader>();
            extReader.Read().Returns(false);

            _command.StubReaders(listReader, defReader, extReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Table Trigger Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("dbo.MyTable.MyTrigger.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_FullTextCatalogsOnly_CastsCatalogs()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "true",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // Single reader: catalog list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("MyCatalog");

            _command.StubReaders(listReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting FullText Catalog Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("MyCatalog.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_FullTextStopListsOnly_CastsStopLists()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "true",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: stop list list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetInt32(0).Returns(1);
            listReader.GetString(1).Returns("MyStopList");

            // 2nd reader: stop words
            var wordsReader = Substitute.For<IDataReader>();
            var wordsCount = 0;
            wordsReader.Read().Returns(_ => wordsCount++ < 1, _ => false);
            wordsReader.GetString(0).Returns("the");
            wordsReader.GetString(1).Returns("English");

            _command.StubReaders(listReader, wordsReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting FullText Stop List Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("MyStopList.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_DDLTriggersOnly_CastsDDLTriggers()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "true",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: DDL trigger list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("MyDDLTrigger");

            // 2nd reader: definition
            var defReader = Substitute.For<IDataReader>();
            defReader.Read().Returns(true, false);
            defReader.IsDBNull(0).Returns(false);
            defReader.GetString(0).Returns("CREATE TRIGGER MyDDLTrigger ON DATABASE FOR CREATE_TABLE AS BEGIN RETURN END");
            defReader.GetBoolean(1).Returns(true);
            defReader.GetBoolean(2).Returns(true);

            // 3rd reader: extended properties (empty)
            var extReader = Substitute.For<IDataReader>();
            extReader.Read().Returns(false);

            _command.StubReaders(listReader, defReader, extReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Database DDL Trigger Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("MyDDLTrigger.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_XmlSchemaCollectionsOnly_CastsCollections()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "true"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: collection list
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("dbo");
            listReader.GetString(1).Returns("MyXmlSchema");

            // ExecuteScalar: XML content
            _command.ExecuteScalar().Returns(_ =>
                KindleGateTestHelpers.IsReadOnlyProbe(_command.CommandText)
                    ? (object)0
                    : "<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"></xsd:schema>");

            // 2nd reader: extended properties (empty)
            var extReader = Substitute.For<IDataReader>();
            extReader.Read().Returns(false);

            _command.StubReaders(listReader, extReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting XML Schema Collection Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("dbo.MyXmlSchema.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_UserDefinedTypesOnly_CastsTypes()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "true",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // 1st reader: alias types list
            var aliasReader = Substitute.For<IDataReader>();
            var aliasCount = 0;
            aliasReader.Read().Returns(_ => aliasCount++ < 1, _ => false);
            aliasReader.GetString(0).Returns("dbo");
            aliasReader.GetString(1).Returns("MyType");
            aliasReader.GetString(2).Returns("varchar");
            aliasReader.GetInt16(3).Returns((short)50);
            aliasReader.GetByte(4).Returns((byte)0);
            aliasReader.GetByte(5).Returns((byte)0);
            aliasReader.GetBoolean(6).Returns(true);

            // 2nd reader: table types list (empty)
            var tableTypeReader = Substitute.For<IDataReader>();
            tableTypeReader.Read().Returns(false);

            _command.StubReaders(aliasReader, tableTypeReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting User Defined Types")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("dbo.MyType.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_SequencesOnly_CastsSequences()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false",
                ["ShouldCast:IndexedViews"] = "false",
                ["ShouldCast:Sequences"] = "true",
                ["ShouldCast:Synonyms"] = "false"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader.GetString(0).Returns("dbo");
            reader.GetString(1).Returns("MySeq");
            reader.GetString(2).Returns("bigint");
            reader.GetValue(3).Returns((object)"1");
            reader.GetValue(4).Returns((object)"1");
            reader.GetValue(5).Returns((object)"1");
            reader.GetValue(6).Returns((object)"9223372036854775807");
            reader.GetBoolean(7).Returns(false);
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Sequences")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("dbo.MySeq.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_SqlServer_SynonymsOnly_CastsSynonyms()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false",
                ["ShouldCast:IndexedViews"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Synonyms"] = "true"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader.GetString(0).Returns("dbo");
            reader.GetString(1).Returns("MySynonym");
            reader.GetString(2).Returns("[OtherDb].[dbo].[OtherTable]");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Synonyms")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("dbo.MySynonym.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region PostgreSQL Tests

    [Test]
    public void CastTemplate_PostgreSQL_AllDisabled_CompletesSuccessfully()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false",
                ["ShouldCast:Collations"] = "false",
                ["ShouldCast:Publications"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Summary")));
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Completed Successfully")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_DefaultsAllTrue_KindlesForge()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL);
            RegisterConnectionFactory(Platform.PostgreSQL);

            var emptyReader = Substitute.For<IDataReader>();
            emptyReader.Read().Returns(false);
            _command.StubReaders(emptyReader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Kindling The Forge")));
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Completed Successfully")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_SchemasOnly_CastsSchemas()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "true",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Schemas");
            reader["FullName"].Returns("myschema");
            reader["Code"].Returns("CREATE SCHEMA IF NOT EXISTS myschema;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Schemas")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("myschema.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_DomainTypesOnly_CastsDomainTypes()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "true",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            // Domain types are DECLARATIVE (F5): the cast enumerates them from pg_type and asks
            // GenerateDomainTypeJSON for each, rather than emitting the guarded CREATE DOMAIN script this
            // test used to assert. That script was the bug -- there is no CREATE OR REPLACE DOMAIN, so once
            // the domain existed the guard skipped and an edited CHECK never landed.
            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["SchemaName"].Returns("public");
            reader["TypeName"].Returns("posint");
            _command.StubReaders(reader);

            var domainJson = "{\"Name\":\"posint\",\"Schema\":\"public\",\"DataType\":\"integer\",\"NotNull\":false,\"CheckConstraints\":[]}";
            _command.ExecuteScalar().Returns(_ =>
                KindleGateTestHelpers.IsReadOnlyProbe(_command.CommandText) ? (object)0 :
                _command.CommandText?.Contains("pg_catalog.pg_class") == true
                    ? (object)0L
                    : (object)domainJson);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Domain Type Structures")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.posint.json")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_EnumTypesOnly_CastsEnumTypes()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "true",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            // Enum types are DECLARATIVE (F5): the cast enumerates them from pg_type and asks
            // GenerateEnumTypeJSON for each one, rather than emitting the guarded CREATE TYPE script
            // this test used to assert. That script was the bug -- once the type existed the guard
            // skipped, so an edited value list did nothing forever.
            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["SchemaName"].Returns("public");
            reader["TypeName"].Returns("mood");
            _command.StubReaders(reader);

            var enumJson = "{\"Name\":\"mood\",\"Schema\":\"public\",\"Values\":[\"happy\",\"sad\"]}";
            _command.ExecuteScalar().Returns(_ =>
                KindleGateTestHelpers.IsReadOnlyProbe(_command.CommandText) ? (object)0 :
                _command.CommandText?.Contains("pg_catalog.pg_class") == true
                    ? (object)0L
                    : (object)enumJson);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Enum Type Structures")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.mood.json")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_CompositeTypesOnly_CastsCompositeTypes()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "true",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Composite Types");
            reader["FullName"].Returns("public.address");
            reader["Code"].Returns("DO $$ BEGIN CREATE TYPE public.address AS (street text, city text); END $$;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Composite Types")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.address.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_FunctionsOnly_CastsFunctions()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "true",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Functions");
            reader["FullName"].Returns("public.my_func");
            reader["Code"].Returns("CREATE OR REPLACE FUNCTION public.my_func() RETURNS INT AS $$ BEGIN RETURN 1; END $$ LANGUAGE plpgsql;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Functions")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_func.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_AggregatesOnly_CastsAggregates()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "true",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Aggregates");
            reader["FullName"].Returns("public.my_agg");
            reader["Code"].Returns("CREATE AGGREGATE public.my_agg(integer) (SFUNC = int4pl, STYPE = int4);");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Aggregates")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_agg.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_ProceduresOnly_CastsProcedures()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "true",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Procedures");
            reader["FullName"].Returns("public.my_proc");
            reader["Code"].Returns("CREATE OR REPLACE PROCEDURE public.my_proc() LANGUAGE plpgsql AS $$ BEGIN END $$;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Procedures")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_proc.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_SequencesOnly_CastsSequences()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "true",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            // Sequences are DECLARATIVE (F5): enumerated from pg_class, then GenerateSequenceJSON per
            // sequence. Note what the JSON does NOT carry -- the current value is data, and a package
            // holding it would reset a live sequence on deploy.
            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["SchemaName"].Returns("public");
            reader["SequenceName"].Returns("my_seq");
            _command.StubReaders(reader);

            var seqJson = "{\"Name\":\"my_seq\",\"Schema\":\"public\",\"DataType\":\"bigint\",\"Increment\":1,\"Cache\":1,\"Cycle\":false}";
            _command.ExecuteScalar().Returns(_ =>
                KindleGateTestHelpers.IsReadOnlyProbe(_command.CommandText) ? (object)0 :
                _command.CommandText?.Contains("pg_catalog.pg_class") == true
                    ? (object)0L
                    : (object)seqJson);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Sequence Structures")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_seq.json")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_RulesOnly_CastsRules()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "true",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Rules");
            reader["FullName"].Returns("public.my_rule");
            reader["Code"].Returns("CREATE OR REPLACE RULE my_rule AS ON INSERT TO public.my_table DO NOTHING;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Rules")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_rule.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_TriggersOnly_CastsTriggers()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "true",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Triggers");
            reader["FullName"].Returns("public.my_trigger");
            reader["Code"].Returns("CREATE TRIGGER my_trigger AFTER INSERT ON public.my_table FOR EACH ROW EXECUTE FUNCTION my_func();");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Triggers")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_trigger.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_ViewsOnly_CastsViews()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "true",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Views");
            reader["FullName"].Returns("public.my_view");
            reader["Code"].Returns("CREATE OR REPLACE VIEW public.my_view AS SELECT 1;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Views")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_view.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_AllDisabled_IncludingMaterializedViews_CompletesSuccessfully()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.DidNotReceive().Info(Arg.Is<string>(s => s.Contains("Casting Materialized View")));
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Completed Successfully")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_MaterializedViewsOnly_CastsMaterializedViews()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "true"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["schemaname"].Returns("public");
            reader["matviewname"].Returns("my_matview");
            _command.StubReaders(reader);

            // Discriminate by CommandText: pg_catalog.pg_class is the kindle-gate existence check
            // (must return 0L so ReadStamp returns null and KindleTheForge runs DDL via ExecuteNonQuery).
            // All other ExecuteScalar calls (GenerateMaterializedViewJson) return the matview JSON.
            var matViewJson = "{\"Name\":\"my_matview\",\"Schema\":\"public\",\"Definition\":\"SELECT 1\",\"WithData\":true}";
            _command.ExecuteScalar().Returns(_ =>
                KindleGateTestHelpers.IsReadOnlyProbe(_command.CommandText) ? (object)0 :
                _command.CommandText?.Contains("pg_catalog.pg_class") == true
                    ? (object)0L
                    : (object)matViewJson);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Materialized View Structures")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_matview.json")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_MaterializedViews_DefaultsToTrue()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL);
            RegisterConnectionFactory(Platform.PostgreSQL);

            var emptyReader = Substitute.For<IDataReader>();
            emptyReader.Read().Returns(false);
            _command.StubReaders(emptyReader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            // With defaults (all true), materialized views should be attempted
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Materialized View Structures")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_CollationsOnly_CastsCollations()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false",
                ["ShouldCast:Collations"] = "true",
                ["ShouldCast:Publications"] = "false"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Collations");
            reader["FullName"].Returns("public.my_collation");
            reader["Code"].Returns("CREATE COLLATION IF NOT EXISTS public.my_collation (LC_COLLATE = 'en_US.utf8', LC_CTYPE = 'en_US.utf8');");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Collations")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("public.my_collation.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_PostgreSQL_PublicationsOnly_CastsPublications()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.PostgreSQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:DomainTypes"] = "false",
                ["ShouldCast:EnumTypes"] = "false",
                ["ShouldCast:CompositeTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Aggregates"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:Sequences"] = "false",
                ["ShouldCast:Rules"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:MaterializedViews"] = "false",
                ["ShouldCast:Collations"] = "false",
                ["ShouldCast:Publications"] = "true"
            });
            RegisterConnectionFactory(Platform.PostgreSQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Publications");
            reader["FullName"].Returns("my_pub");
            reader["Code"].Returns("DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'my_pub') THEN CREATE PUBLICATION my_pub FOR ALL TABLES WITH (publish = 'insert,update,delete,truncate'); END IF; END $$;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.PostgreSQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Publications")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("my_pub.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region MySQL Tests

    [Test]
    public void CastTemplate_MySQL_AllDisabled_CompletesSuccessfully()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["Source:Database"] = "testdb",
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false"
            });
            RegisterConnectionFactory(Platform.MySQL);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Summary")));
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Completed Successfully")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_MySQL_FunctionsOnly_CastsFunctions()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "true",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false"
            });
            RegisterConnectionFactory(Platform.MySQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Functions");
            reader["FullName"].Returns("my_func");
            reader["Code"].Returns("CREATE FUNCTION my_func() RETURNS INT RETURN 1;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Function Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("my_func.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_MySQL_ViewsOnly_CastsViews()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "true",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false"
            });
            RegisterConnectionFactory(Platform.MySQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Views");
            reader["FullName"].Returns("my_view");
            reader["Code"].Returns("CREATE VIEW my_view AS SELECT 1;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting View Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("my_view.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_MySQL_ProceduresOnly_CastsProcedures()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "true",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false"
            });
            RegisterConnectionFactory(Platform.MySQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Procedures");
            reader["FullName"].Returns("my_proc");
            reader["Code"].Returns("CREATE PROCEDURE my_proc() BEGIN END;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Stored Procedure Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("my_proc.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_MySQL_TriggersOnly_CastsTriggers()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "true",
                ["ShouldCast:Events"] = "false"
            });
            RegisterConnectionFactory(Platform.MySQL);

            var reader = Substitute.For<IDataReader>();
            var callCount = 0;
            reader.Read().Returns(_ => callCount++ < 1, _ => false);
            reader["Folder"].Returns("Triggers");
            reader["FullName"].Returns("my_trigger");
            reader["Code"].Returns("CREATE TRIGGER my_trigger BEFORE INSERT ON t FOR EACH ROW BEGIN END;");
            _command.StubReaders(reader);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Trigger Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("my_trigger.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_MySQL_EventsOnly_CastsEvents()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            // NOT StubMySqlKindleGate(): this test stubs ExecuteScalar ONCE, below, covering the gate's
            // answers and the event JSON together. Layering a second Returns over the gate's lambda does
            // not replace it -- the setup call runs the existing lambda, which touches the substitute
            // itself, and NSubstitute then attaches the new Returns to THAT call instead of to
            // ExecuteScalar. The symptom is a stub that silently never fires.
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "true"
            });
            RegisterConnectionFactory(Platform.MySQL);

            // Events are DECLARATIVE (F4): enumerated from INFORMATION_SCHEMA.EVENTS, then
            // SchemaSmith_GenerateEventJson per event, rather than the CREATE EVENT script this test
            // used to assert.
            //
            // Read() is keyed on the COMMAND rather than a bare call counter: the cast issues several
            // reader queries before it reaches events, and a counter would already be spent by then --
            // the row would silently never arrive and the test would fail for the wrong reason.
            var reader = Substitute.For<IDataReader>();
            var eventRowsRead = 0;
            reader.Read().Returns(_ =>
                (_command.CommandText ?? string.Empty).Contains("INFORMATION_SCHEMA.EVENTS") && eventRowsRead++ < 1);
            // The event enumeration reads by ORDINAL (GetString(0)), not by name.
            reader.GetString(0).Returns("my_event");
            // NOT StubReaders: that hands the reader back ONCE and an exhausted one forever after, so an
            // earlier query in the MySQL cast consumed it and the events read saw no rows. Returning this
            // reader for every call is safe precisely because Read() is keyed on the command -- it yields
            // a row to the EVENTS query and nothing to anything else, which is the discrimination
            // StubReaders exists to provide.
            _command.ExecuteReader().Returns(_ => reader);

            // The kindle gate's answers and the event JSON in one lambda -- see the note above on why
            // this cannot be layered on top of StubMySqlKindleGate.
            var eventJson = "{\"Name\":\"my_event\",\"Schedule\":\"EVERY 1 HOUR\",\"Body\":\"SELECT 1\"}";
            var stamp = ForgeKindler.ComputeKindleStamp(Platform.MySQL);
            _command.ExecuteScalar().Returns(_ =>
            {
                var sql = _command.CommandText ?? string.Empty;
                if (sql.Contains("GenerateEventJSON")) return (object)eventJson;
                if (KindleGateTestHelpers.IsReadOnlyProbe(sql)) return (object)0;
                if (sql.Contains("GET_LOCK")) return (object)1L;
                if (sql.Contains("information_schema.tables")) return (object)1L;
                if (sql.Contains("SchemaSmith_KindleStamp") && sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                    return (object)stamp;
                return null;
            });

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Event Structures")));
            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Cast Json for event")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("my_event.json")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_MySQL_TableExtraction_WritesJson()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "true",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false"
            });
            RegisterConnectionFactory(Platform.MySQL);

            // First reader: table list query
            var tableListReader = Substitute.For<IDataReader>();
            var tableReadCount = 0;
            tableListReader.Read().Returns(_ => tableReadCount++ < 1, _ => false);
            tableListReader["TABLE_SCHEMA"].Returns("testdb");
            tableListReader["TABLE_NAME"].Returns("users");

            // Second reader: GenerateTableJSON result
            var jsonReader = Substitute.For<IDataReader>();
            var jsonReadCount = 0;
            jsonReader.Read().Returns(_ => jsonReadCount++ < 1, _ => false);
            // Column has no IsPrimaryKey — a primary key is a Table-level Index (PrimaryKey:true),
            // not a column flag — and the real property is Nullable, not IsNullable.
            jsonReader[0].Returns("{\"Name\":\"users\",\"Columns\":[{\"Name\":\"id\",\"DataType\":\"INT\",\"Nullable\":false}]}");

            // Second connection for JSON extraction
            var connection2 = Substitute.For<IDbConnection>();
            var command2 = Substitute.For<IDbCommand>();
            connection2.CreateCommand().Returns(command2);

            var connectionCallCount = 0;
            _connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(_ =>
            {
                connectionCallCount++;
                return connectionCallCount <= 1 ? _connection : connection2;
            });

            _command.StubReaders(tableListReader);
            command2.StubReaders(jsonReader);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Extracting users")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("users.json")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_MySQL_EmptyJsonReturn_LogsError()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "true",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false"
            });
            RegisterConnectionFactory(Platform.MySQL);

            var tableListReader = Substitute.For<IDataReader>();
            var tableReadCount = 0;
            tableListReader.Read().Returns(_ => tableReadCount++ < 1, _ => false);
            tableListReader["TABLE_SCHEMA"].Returns("testdb");
            tableListReader["TABLE_NAME"].Returns("bad_table");

            var jsonReader = Substitute.For<IDataReader>();
            jsonReader.Read().Returns(false);

            var connection2 = Substitute.For<IDbConnection>();
            var command2 = Substitute.For<IDbCommand>();
            connection2.CreateCommand().Returns(command2);

            var connectionCallCount = 0;
            _connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(_ =>
            {
                connectionCallCount++;
                return connectionCallCount <= 1 ? _connection : connection2;
            });

            _command.StubReaders(tableListReader);
            command2.StubReaders(jsonReader);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Error(Arg.Is<string>(s => s.Contains("No json returned for bad_table")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_ObjectListFilter_ParsesCorrectly()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false",
                ["ShouldCast:ObjectList"] = "table1,table2;table3"
            });
            RegisterConnectionFactory(Platform.MySQL);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region MariaDb Tests

    [Test]
    public void CastTemplate_MariaDb_SequencesOnly_CastsSequences()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MariaDb, new Dictionary<string, string>
            {
                ["Source:Database"] = "testdb",
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false",
                ["ShouldCast:Sequences"] = "true"
            });
            RegisterConnectionFactory(Platform.MariaDb);

            // 1st reader: sequence name list (INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'SEQUENCE')
            var listReader = Substitute.For<IDataReader>();
            var listCount = 0;
            listReader.Read().Returns(_ => listCount++ < 1, _ => false);
            listReader.GetString(0).Returns("my_seq");

            // 2nd reader: SHOW CREATE SEQUENCE result
            var showCreateReader = Substitute.For<IDataReader>();
            var showCreateCount = 0;
            showCreateReader.Read().Returns(_ => showCreateCount++ < 1, _ => false);
            showCreateReader.GetString(1).Returns(
                "CREATE SEQUENCE `testdb`.`my_seq` start with 1 minvalue 1 maxvalue 9223372036854775806 increment by 1 cache 1000 nocycle ENGINE=InnoDB");

            _command.StubReaders(listReader, showCreateReader);

            var tongs = new SchemaTongs(Platform.MariaDb);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Casting Sequence Scripts")));
            _fileWrapper.Received().WriteAllText(Arg.Is<string>(s => s.Contains("my_seq.sql")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_MariaDb_SequencesFlag_IgnoredOnPlainMySQL()
    {
        // Same ShouldCast:Sequences=true config, but Platform.MySQL — the flag must have no effect
        // there, since MySQL has no native SEQUENCE object at all (not a version gap).
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            StubMySqlKindleGate();
            RegisterConfig(Platform.MySQL, new Dictionary<string, string>
            {
                ["Source:Database"] = "testdb",
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Events"] = "false",
                ["ShouldCast:Sequences"] = "true"
            });
            RegisterConnectionFactory(Platform.MySQL);

            var tongs = new SchemaTongs(Platform.MySQL);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            _progressLog.DidNotReceive().Info(Arg.Is<string>(s => s.Contains("Casting Sequence Scripts")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region Extended Property Filter Tests

    [Test]
    public void GetExtendedProperties_FiltersOutInternalEPs()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);
            RegisterConnectionFactory(Platform.SqlServer);

            var reader = Substitute.For<IDataReader>();
            var readCount = 0;
            reader.Read().Returns(_ =>
            {
                readCount++;
                return readCount <= 2;
            });
            reader.GetString(0).Returns("ProductName", "MS_Description");
            reader.GetString(1).Returns("TestProduct", "A test description");

            _command.StubReaders(reader);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            var result = tongs.GetExtendedProperties(_command, "SCHEMA", "dbo");

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Contain("MS_Description"));
                Assert.That(result, Does.Not.Contain("ProductName"));
            });

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void GetExtendedProperties_AllInternalEPs_ReturnsEmpty()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);
            RegisterConnectionFactory(Platform.SqlServer);

            var reader = Substitute.For<IDataReader>();
            var readCount = 0;
            reader.Read().Returns(_ =>
            {
                readCount++;
                return readCount <= 1;
            });
            reader.GetString(0).Returns("ProductName");
            reader.GetString(1).Returns("TestProduct");

            _command.StubReaders(reader);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            var result = tongs.GetExtendedProperties(_command, "SCHEMA", "dbo");

            Assert.That(result, Is.EqualTo(""));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region CheckConstraintStyle Config and Mismatch Tests

    [Test]
    public void CastTemplate_NewProduct_ConfigTableLevel_SetsProductTableLevelAndEffectiveStyle()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false",
                ["Product:CheckConstraintStyle"] = "TableLevel"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // New product — Product.json does not exist until written
            var productWritten = false;
            string writtenJson = null;
            _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(_ => productWritten);
            _fileWrapper.WriteAllText(Arg.Is<string>(s => s.Contains("Product.json")), Arg.Do<string>(j =>
            {
                writtenJson = j;
                productWritten = true;
            }));
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(_ => writtenJson ?? "{}");

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            Assert.That(tongs.CheckConstraintStyle, Is.EqualTo(CheckConstraintStyle.TableLevel));
            _progressLog.DidNotReceive().Warn(Arg.Is<string>(s => s.Contains("CheckConstraintStyle")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_ExistingProductColumnLevel_ConfigTableLevel_WarnsAndUsesProductValue()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false",
                ["Product:CheckConstraintStyle"] = "TableLevel"
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // Existing product with ColumnLevel (default, no explicit value in JSON)
            _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(true);
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(
                "{\"Name\":\"TestProduct\",\"Platform\":\"SqlServer\",\"TemplateOrder\":[\"TestTemplate\"],\"ScriptTokens\":{},\"ScriptFolders\":[]}");

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            Assert.That(tongs.CheckConstraintStyle, Is.EqualTo(CheckConstraintStyle.ColumnLevel));
            _progressLog.Received().Warn(Arg.Is<string>(s =>
                s.Contains("CheckConstraintStyle") &&
                s.Contains("TableLevel") &&
                s.Contains("ColumnLevel") &&
                s.Contains("Product.json")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_ExistingProductTableLevel_ConfigEmpty_NoWarningUsesProductValue()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
                // No Product:CheckConstraintStyle — config unset
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // Existing product with TableLevel
            _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(true);
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(
                "{\"Name\":\"TestProduct\",\"Platform\":\"SqlServer\",\"CheckConstraintStyle\":\"TableLevel\",\"TemplateOrder\":[\"TestTemplate\"],\"ScriptTokens\":{},\"ScriptFolders\":[]}");

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            Assert.That(tongs.CheckConstraintStyle, Is.EqualTo(CheckConstraintStyle.TableLevel));
            _progressLog.DidNotReceive().Warn(Arg.Is<string>(s => s.Contains("CheckConstraintStyle")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void CastTemplate_NewProduct_ConfigEmpty_EffectiveStyleIsColumnLevel()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "false",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false"
                // No Product:CheckConstraintStyle
            });
            RegisterConnectionFactory(Platform.SqlServer);

            // New product — Product.json does not exist until written
            var productWritten = false;
            string writtenJson = null;
            _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(_ => productWritten);
            _fileWrapper.WriteAllText(Arg.Is<string>(s => s.Contains("Product.json")), Arg.Do<string>(j =>
            {
                writtenJson = j;
                productWritten = true;
            }));
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(_ => writtenJson ?? "{}");

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            Assert.DoesNotThrow(() => tongs.CastTemplate());

            Assert.That(tongs.CheckConstraintStyle, Is.EqualTo(CheckConstraintStyle.ColumnLevel));
            _progressLog.DidNotReceive().Warn(Arg.Is<string>(s => s.Contains("CheckConstraintStyle")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion

    #region FolderMapping Resolution Tests

    [Test]
    public void ResolveFolderMappings_TemplateHasType_ConfigDiffers_WarnsAndUsesTemplatePath()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["FolderMapping:Views"] = "DatabaseViews"
            });

            // Template has Views with ObjectType=Views at the default "Views" path
            var templateJson = "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[" +
                               "{\"FolderPath\":\"Views\",\"QuenchSlot\":\"Objects\",\"ObjectType\":\"Views\"}" +
                               "],\"ScriptTokens\":{}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            tongs.ResolveFolderMappings();

            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Views], Is.EqualTo("Views"));
            _progressLog.Received().Warn(Arg.Is<string>(s =>
                s.Contains("Views") &&
                s.Contains("DatabaseViews") &&
                s.Contains("template")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolveFolderMappings_TemplateMissingType_AddsFolderToTemplate()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);

            // Template has no script folders at all
            var templateJson = "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[],\"ScriptTokens\":{}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            string writtenTemplateJson = null;
            _fileWrapper.WriteAllText(Arg.Is<string>(s => s.Contains("Template.json")), Arg.Do<string>(j => writtenTemplateJson = j));

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            tongs.ResolveFolderMappings();

            // Should have resolved Views to its default name
            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Views], Is.EqualTo("Views"));

            // Template.json should have been written with the new folders
            Assert.That(writtenTemplateJson, Is.Not.Null);
            Assert.That(writtenTemplateJson, Does.Contain("Views"));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolveFolderMappings_NewTemplateWithAllDefaults_NoAdditions()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);

            // Template has all default folders with ObjectType set
            var defaults = Template.GetDefaultTemplateFolders(Platform.SqlServer);
            var foldersJson = Newtonsoft.Json.JsonConvert.SerializeObject(defaults);
            var templateJson = $"{{\"Name\":\"TestTemplate\",\"ScriptFolders\":{foldersJson},\"ScriptTokens\":{{}}}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            tongs.ResolveFolderMappings();

            // All typed folders should be resolved
            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Views], Is.EqualTo("Views"));
            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Procedures], Is.EqualTo("Procedures"));

            // Template.json should NOT have been re-written (no modifications)
            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.Contains("Template.json")), Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolveFolderMappings_NoFolderMappingConfig_ResolvesFromTemplateDefaults()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);
            // No FolderMapping config keys — defaults only

            var templateJson = "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[" +
                               "{\"FolderPath\":\"Views\",\"QuenchSlot\":\"Objects\",\"ObjectType\":\"Views\"}," +
                               "{\"FolderPath\":\"Procedures\",\"QuenchSlot\":\"Objects\",\"ObjectType\":\"Procedures\"}" +
                               "],\"ScriptTokens\":{}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            tongs.ResolveFolderMappings();

            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Views], Is.EqualTo("Views"));
            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Procedures], Is.EqualTo("Procedures"));
            _progressLog.DidNotReceive().Warn(Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolveFolderMappings_ConfigMatchesDefault_NoWarning()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["FolderMapping:Views"] = "Views" // Same as default
            });

            var templateJson = "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[" +
                               "{\"FolderPath\":\"Views\",\"QuenchSlot\":\"Objects\",\"ObjectType\":\"Views\"}" +
                               "],\"ScriptTokens\":{}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            tongs.ResolveFolderMappings();

            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Views], Is.EqualTo("Views"));
            _progressLog.DidNotReceive().Warn(Arg.Any<string>());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolveFolderMappings_PostgreSQL_ResolvesAllTypes()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            var configValues = new Dictionary<string, string>
            {
                ["Source:Server"] = "localhost",
                ["Source:Database"] = "testdb",
                ["Source:User"] = "testuser",
                ["Source:Password"] = "testpass",
                ["Target:Platform"] = "PostgreSQL",
                ["Product:Path"] = Path.GetTempPath(),
                ["Product:Name"] = "TestProduct",
                ["Template:Name"] = "TestTemplate"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            FactoryContainer.Register<IConfigurationRoot>(config);
            FactoryContainer.Register(_environment);
            FactoryContainer.Register(_fileWrapper);
            FactoryContainer.Register(_directoryWrapper);
            LogFactory.Register("ProgressLog", _progressLog);
            LogFactory.Register("ErrorLog", _errorLog);

            _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(true);

            // Empty template — all types should be added
            var templateJson = "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[],\"ScriptTokens\":{}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.PostgreSQL);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            tongs.ResolveFolderMappings();

            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Views], Is.EqualTo("Views"));
            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Functions], Is.EqualTo("Functions"));
            Assert.That(tongs.ResolvedFolders[ScriptObjectType.TriggerFunctions], Is.EqualTo("Trigger Functions"));
            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Sequences], Is.EqualTo("Sequences"));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolveFolderMappings_TemplateMissingType_CreatesDirectory()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);

            var templateJson = "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[],\"ScriptTokens\":{}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            var templatePath = Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate");
            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(templatePath);
            tongs.ResolveFolderMappings();

            _directoryWrapper.Received().CreateDirectory(
                Arg.Is<string>(s => s.Contains("Views")));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void ResolveFolderMappings_AddedFolder_HasCorrectQuenchSlot()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);

            var templateJson = "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[],\"ScriptTokens\":{}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            string writtenJson = null;
            _fileWrapper.WriteAllText(Arg.Is<string>(s => s.Contains("Template.json")), Arg.Do<string>(j => writtenJson = j));

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            tongs.ResolveFolderMappings();

            // Triggers should be AfterTablesObjects for SQL Server
            Assert.That(writtenJson, Does.Contain("Triggers"));
            var writtenTemplate = Newtonsoft.Json.JsonConvert.DeserializeObject<Template>(writtenJson);
            var triggerFolder = writtenTemplate.ScriptFolders.Find(f => f.ObjectType == ScriptObjectType.Triggers);
            Assert.That(triggerFolder, Is.Not.Null);
            Assert.That(triggerFolder.QuenchSlot, Is.EqualTo(TemplateQuenchSlot.AfterTablesObjects));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void GetCastPath_WithResolvedFolder_ReturnsResolvedPath()
    {
        var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
        tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
        tongs.ResolvedFolders[ScriptObjectType.Views] = "DatabaseViews";

        var result = tongs.GetCastPath(ScriptObjectType.Views, "Views");
        Assert.That(result, Is.EqualTo(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate", "DatabaseViews")));
    }

    [Test]
    public void GetCastPath_WithoutResolvedFolder_ReturnsDefaultPath()
    {
        var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
        tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));

        var result = tongs.GetCastPath(ScriptObjectType.Views, "Views");
        Assert.That(result, Is.EqualTo(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate", "Views")));
    }

    [Test]
    public void GetCastPath_NoneType_AlwaysReturnsDefault()
    {
        var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
        tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));

        var result = tongs.GetCastPath(ScriptObjectType.None, "Tables");
        Assert.That(result, Is.EqualTo(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate", "Tables")));
    }

    [Test]
    public void ResolveFolderMappings_TemplateNotFound_NoException()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer);

            _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(false);
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns((string)null);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            Assert.DoesNotThrow(() => tongs.ResolveFolderMappings());

            Assert.That(tongs.ResolvedFolders, Is.Empty);

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void GetFullyExtractedFolders_UsesResolvedFolderNames()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["FolderMapping:Views"] = "DatabaseViews",
                ["ShouldCast:Tables"] = "false",
                ["ShouldCast:Schemas"] = "false",
                ["ShouldCast:UserDefinedTypes"] = "false",
                ["ShouldCast:Functions"] = "false",
                ["ShouldCast:Views"] = "true",
                ["ShouldCast:Procedures"] = "false",
                ["ShouldCast:TableTriggers"] = "false",
                ["ShouldCast:Catalogs"] = "false",
                ["ShouldCast:StopLists"] = "false",
                ["ShouldCast:DDLTriggers"] = "false",
                ["ShouldCast:XMLSchemaCollections"] = "false",
                ["ShouldCast:IndexedViews"] = "false"
            });

            var templateJson = "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[" +
                               "{\"FolderPath\":\"DatabaseViews\",\"QuenchSlot\":\"Objects\",\"ObjectType\":\"Views\"}" +
                               "],\"ScriptTokens\":{}}";
            _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(templateJson);

            var tongs = new global::SchemaTongs.SchemaTongs(Platform.SqlServer);
            tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
            tongs.ResolveFolderMappings();

            // Verify resolved folder name is used, not the default
            Assert.That(tongs.ResolvedFolders[ScriptObjectType.Views], Is.EqualTo("DatabaseViews"));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void PreFlightSourceVersion_BelowFloor_ThrowsClearMessage()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["Source:Server"] = "SQL2K5",
                ["Source:Database"] = "LegacyDb"
            });
            RegisterConnectionFactory(Platform.SqlServer);
            _command.ExecuteScalar().Returns("9");   // SQL Server 2005, below the 2008 floor

            var tongs = new SchemaTongs(Platform.SqlServer);

            var ex = Assert.Throws<RunFailedException>(() => tongs.PreFlightSourceVersion());
            Assert.That(ex!.Message, Does.Contain("below the minimum supported"));
            Assert.That(ex.Message, Does.Contain("SQL2K5"));

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void PreFlightSourceVersion_SupportedVersion_DoesNotThrow()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            SetUpMocks();
            RegisterConfig(Platform.SqlServer, new Dictionary<string, string>
            {
                ["Source:Database"] = "ModernDb"
            });
            RegisterConnectionFactory(Platform.SqlServer);
            _command.ExecuteScalar().Returns("16", 150);   // SQL Server 2022, compat 150 (>= 140)

            var tongs = new SchemaTongs(Platform.SqlServer);

            Assert.DoesNotThrow(() => tongs.PreFlightSourceVersion());

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    #endregion
}
