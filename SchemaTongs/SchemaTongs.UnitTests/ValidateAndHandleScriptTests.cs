// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.DataAccess;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.UnitTests;

[TestFixture]
public class ValidateAndHandleScriptTests
{
    private ILog _progressLog;
    private IFile _fileWrapper;
    private IDirectory _directoryWrapper;
    private IDbConnection _connection;
    private IDbTransaction _transaction;
    private IDbCommand _validationCommand;

    [SetUp]
    public void SetUp()
    {
        _progressLog = Substitute.For<ILog>();
        _fileWrapper = Substitute.For<IFile>();
        _directoryWrapper = Substitute.For<IDirectory>();
        _connection = Substitute.For<IDbConnection>();
        _transaction = Substitute.For<IDbTransaction>();
        _validationCommand = Substitute.For<IDbCommand>();

        _connection.BeginTransaction().Returns(_transaction);
        _connection.CreateCommand().Returns(_validationCommand);
    }

    [TearDown]
    public void TearDown()
    {
        _validationCommand?.Dispose();
        _transaction?.Dispose();
        _connection?.Dispose();
    }

    private SchemaTongs CreateTongs(Platform platform, bool validateScripts, bool saveInvalidScripts = true)
    {
        FactoryContainer.Register(_fileWrapper);
        FactoryContainer.Register(_directoryWrapper);
        LogFactory.Register("ProgressLog", _progressLog);

        var tongs = new SchemaTongs(platform);
        tongs._validateScripts = validateScripts;
        tongs._saveInvalidScripts = saveInvalidScripts;
        return tongs;
    }

    private void CleanUp()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void ValidateAndHandleScript_ValidationDisabled_NoValidationOccurs()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: false);

            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.MyView.sql",
                "CREATE VIEW [dbo].[MyView] AS SELECT 1", ScriptObjectType.Views);

            _connection.DidNotReceive().BeginTransaction();
            Assert.That(tongs._invalidScripts, Is.Empty);

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_ValidScript_NoErrorFileCreated()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true);

            // Script validation succeeds (no exception thrown)
            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.MyView.sql",
                "CREATE VIEW [dbo].[MyView] AS SELECT 1", ScriptObjectType.Views);

            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWith(".sqlerror")), Arg.Any<string>());
            _fileWrapper.DidNotReceive().Delete(Arg.Any<string>());
            Assert.That(tongs._invalidScripts, Is.Empty);

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_InvalidScript_SaveEnabled_WritesSqulerrorAndDeletesSql()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true, saveInvalidScripts: true);

            _validationCommand.ExecuteNonQuery().Returns(_ => throw new Exception("Syntax error"));
            _fileWrapper.Exists(Arg.Any<string>()).Returns(true);

            var scriptPath = Path.Combine("C:", "test", "dbo.MyView.sql");
            tongs.ValidateAndHandleScript(_connection, scriptPath,
                "CREATE VIEW [dbo].[MyView] AS INVALID SQL", ScriptObjectType.Views);

            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.EndsWith("dbo.MyView.sqlerror")),
                Arg.Is<string>(s => s.Contains("CREATE VIEW")));
            _fileWrapper.Received().Delete(Arg.Is<string>(s => s.EndsWith("dbo.MyView.sql")));
            Assert.That(tongs._invalidScripts, Has.Count.EqualTo(1));
            Assert.That(tongs._invalidScripts[0].FileName, Is.EqualTo("dbo.MyView.sql"));
            Assert.That(tongs._invalidScripts[0].ObjectType, Is.EqualTo(ScriptObjectType.Views));

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_InvalidScript_SaveDisabled_DeletesSqlNoSqulerror()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true, saveInvalidScripts: false);

            _validationCommand.ExecuteNonQuery().Returns(_ => throw new Exception("Syntax error"));
            _fileWrapper.Exists(Arg.Any<string>()).Returns(true);

            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.MyView.sql",
                "CREATE VIEW [dbo].[MyView] AS INVALID SQL", ScriptObjectType.Views);

            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWith(".sqlerror")), Arg.Any<string>());
            _fileWrapper.Received().Delete(@"C:\test\dbo.MyView.sql");
            Assert.That(tongs._invalidScripts, Has.Count.EqualTo(1));

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_InvalidScript_LogsWarning()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true);

            _validationCommand.ExecuteNonQuery().Returns(_ => throw new Exception("Syntax error near 'INVALID'"));
            _fileWrapper.Exists(Arg.Any<string>()).Returns(true);

            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.BadView.sql",
                "CREATE VIEW [dbo].[BadView] AS INVALID", ScriptObjectType.Views);

            _progressLog.Received().Warn(Arg.Is<string>(s =>
                s.Contains("dbo.BadView.sql") && s.Contains("Syntax error near 'INVALID'")));
            // The package will not contain a deployable script for the view, so the run is incomplete.
            Assert.That(tongs.ExitCode, Is.EqualTo(1));

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_SkippedType_NoValidation()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true);

            // Schemas is a skipped type (not in GuidRenameTypes or ParseOnlyTypes)
            tongs.ValidateAndHandleScript(_connection, @"C:\test\TestSchema.sql",
                "IF NOT EXISTS ... CREATE SCHEMA [TestSchema]", ScriptObjectType.Schemas);

            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.EndsWith(".sqlerror")), Arg.Any<string>());
            Assert.That(tongs._invalidScripts, Is.Empty);

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_InvalidSaveInvalidScriptsFalse_DeletesFile()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true, saveInvalidScripts: false);

            _validationCommand.ExecuteNonQuery().Returns(_ => throw new Exception("Error"));

            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.MyView.sql",
                "CREATE VIEW [dbo].[MyView] AS BAD", ScriptObjectType.Views);

            // Delete is unconditional — no file.Exists check needed (we always wrote the file just before)
            _fileWrapper.Received().Delete(@"C:\test\dbo.MyView.sql");

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_MultipleInvalid_AccumulatesInList()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true);

            _validationCommand.ExecuteNonQuery().Returns(_ => throw new Exception("Error"));
            _fileWrapper.Exists(Arg.Any<string>()).Returns(true);

            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.View1.sql",
                "CREATE VIEW [dbo].[View1] AS BAD", ScriptObjectType.Views);
            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.Func1.sql",
                "CREATE FUNCTION [dbo].[Func1]() RETURNS INT AS BAD", ScriptObjectType.Functions);

            Assert.That(tongs._invalidScripts, Has.Count.EqualTo(2));
            Assert.That(tongs._invalidScripts[0].ObjectType, Is.EqualTo(ScriptObjectType.Views));
            Assert.That(tongs._invalidScripts[1].ObjectType, Is.EqualTo(ScriptObjectType.Functions));

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_UnchangedKnownBad_SkipsValidation()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true);
            var script = "CREATE VIEW [dbo].[BadView] AS SELECT * FROM NonExistent";

            // .sqlerror file exists with same content
            _fileWrapper.Exists(@"C:\test\dbo.BadView.sqlerror").Returns(true);
            _fileWrapper.ReadAllText(@"C:\test\dbo.BadView.sqlerror").Returns(script);
            _fileWrapper.Exists(@"C:\test\dbo.BadView.sql").Returns(true);

            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.BadView.sql",
                script, ScriptObjectType.Views);

            // Should NOT call BeginTransaction (no validation attempt)
            _connection.DidNotReceive().BeginTransaction();
            // Should still be added to invalid list
            Assert.That(tongs._invalidScripts, Has.Count.EqualTo(1));
            Assert.That(tongs._invalidScripts[0].ErrorMessage, Does.Contain("unchanged"));
            // Should delete the .sql file since we know it's still bad
            _fileWrapper.Received().Delete(@"C:\test\dbo.BadView.sql");

            CleanUp();
        }
    }

    [Test]
    public void ValidateAndHandleScript_ChangedKnownBad_RevalidatesScript()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true);
            var oldScript = "CREATE VIEW [dbo].[BadView] AS SELECT * FROM OldNonExistent";
            var newScript = "CREATE VIEW [dbo].[BadView] AS SELECT 1 AS Val";

            // .sqlerror file exists with DIFFERENT content
            _fileWrapper.Exists(@"C:\test\dbo.BadView.sqlerror").Returns(true);
            _fileWrapper.ReadAllText(@"C:\test\dbo.BadView.sqlerror").Returns(oldScript);
            _fileWrapper.Exists(@"C:\test\dbo.BadView.sql").Returns(false);

            // New script validates successfully
            tongs.ValidateAndHandleScript(_connection, @"C:\test\dbo.BadView.sql",
                newScript, ScriptObjectType.Views);

            // SHOULD call BeginTransaction (validation runs)
            _connection.Received().BeginTransaction();
            // Script is now valid — should NOT be in invalid list
            Assert.That(tongs._invalidScripts, Is.Empty);

            CleanUp();
        }
    }

    [Test]
    public void ShouldSkipKnownBadScript_ValidationOff_SqulerrorExists_ReturnsTrue()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: false);
            _fileWrapper.Exists(@"C:\test\dbo.BadView.sqlerror").Returns(true);

            var result = tongs.ShouldSkipKnownBadScript(@"C:\test\dbo.BadView.sql");

            Assert.That(result, Is.True);

            Assert.That(tongs.ExitCode, Is.EqualTo(1), "a skipped export leaves the object out of the package");
            CleanUp();
        }
    }

    [Test]
    public void ShouldSkipKnownBadScript_ValidationOff_NoSqulerror_ReturnsFalse()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: false);
            _fileWrapper.Exists(@"C:\test\dbo.GoodView.sqlerror").Returns(false);

            var result = tongs.ShouldSkipKnownBadScript(@"C:\test\dbo.GoodView.sql");

            Assert.That(result, Is.False);

            CleanUp();
        }
    }

    [Test]
    public void ShouldSkipKnownBadScript_ValidationOn_SqulerrorExists_ReturnsFalse()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            // When validation is ON, always extract — validation handles the rest
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: true);
            _fileWrapper.Exists(@"C:\test\dbo.BadView.sqlerror").Returns(true);

            var result = tongs.ShouldSkipKnownBadScript(@"C:\test\dbo.BadView.sql");

            Assert.That(result, Is.False);

            CleanUp();
        }
    }

    // An encrypted module cannot be scripted by anyone. It is skipped with a warning, and the run reports itself
    // incomplete (exit 1) because the package will not contain it.
    [Test]
    public void EncryptedModule_IsSkipped_AndTheRunExitsOne()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer, validateScripts: false);
            var command = Substitute.For<IDbCommand>();
            var reader = Substitute.For<IDataReader>();
            reader.Read().Returns(true);
            reader.IsDBNull(0).Returns(true);
            command.ExecuteReader().Returns(reader);

            var script = tongs.ScriptSqlServerProgrammableObject(command, "dbo", "SecretProc", "PROCEDURE");

            Assert.Multiple(() =>
            {
                Assert.That(script, Is.Null);
                Assert.That(tongs.ExitCode, Is.EqualTo(1));
            });
            _progressLog.Received().Warn(Arg.Is<string>(s => s.Contains("dbo.SecretProc is encrypted")));

            CleanUp();
        }
    }
}

[TestFixture]
public class GenerateInvalidObjectCleanupScriptTests
{
    private ILog _progressLog;
    private IFile _fileWrapper;
    private IDirectory _directoryWrapper;

    [SetUp]
    public void SetUp()
    {
        _progressLog = Substitute.For<ILog>();
        _fileWrapper = Substitute.For<IFile>();
        _directoryWrapper = Substitute.For<IDirectory>();
    }

    private SchemaTongs CreateTongs(Platform platform)
    {
        var configValues = new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Product:Path"] = Path.GetTempPath(),
            ["Product:Name"] = "TestProduct",
            ["Template:Name"] = "TestTemplate"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        FactoryContainer.Register<IConfigurationRoot>(config);
        FactoryContainer.Register(_fileWrapper);
        FactoryContainer.Register(_directoryWrapper);
        LogFactory.Register("ProgressLog", _progressLog);

        var tongs = new SchemaTongs(platform);
        tongs.SetTemplatePath(Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate"));
        return tongs;
    }

    private void CleanUp()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void GenerateInvalidObjectCleanupScript_NoInvalids_NoFileWritten()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer);

            tongs.GenerateInvalidObjectCleanupScript();

            _fileWrapper.DidNotReceive().WriteAllText(
                Arg.Is<string>(s => s.Contains("_InvalidObjectCleanup")), Arg.Any<string>());

            CleanUp();
        }
    }

    [Test]
    public void GenerateInvalidObjectCleanupScript_WithInvalids_WritesCleanupScript()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer);
            tongs._invalidScripts.Add(("dbo.BadView.sql", "Syntax error", ScriptObjectType.Views));
            tongs._invalidScripts.Add(("dbo.BadFunc.sql", "Parse error", ScriptObjectType.Functions));

            _directoryWrapper.Exists(Arg.Any<string>()).Returns(false);

            tongs.GenerateInvalidObjectCleanupScript();

            _directoryWrapper.Received().CreateDirectory(
                Arg.Is<string>(s => s.Contains("Logs")));
            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.Contains("_InvalidObjectCleanup.sql")),
                Arg.Is<string>(s => s.Contains("DROP VIEW") && s.Contains("DROP FUNCTION") && s.Contains("2 invalid objects")));

            CleanUp();
        }
    }

    [Test]
    public void GenerateInvalidObjectCleanupScript_ArchivesExistingScript()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.SqlServer);
            tongs._invalidScripts.Add(("dbo.BadView.sql", "Error", ScriptObjectType.Views));

            var logsDir = Path.Combine(Path.GetTempPath(), "TestProduct", "TestTemplate", "Logs");
            _directoryWrapper.Exists(Arg.Is<string>(s => s.Contains("Logs"))).Returns(true);
            _directoryWrapper.GetFiles(logsDir, "_InvalidObjectCleanup.sql", SearchOption.TopDirectoryOnly)
                .Returns(new[] { Path.Combine(logsDir, "_InvalidObjectCleanup.sql") });

            tongs.GenerateInvalidObjectCleanupScript();

            _fileWrapper.Received().Move(
                Arg.Is<string>(s => s.Contains("_InvalidObjectCleanup.sql")),
                Arg.Is<string>(s => s.Contains("_InvalidObjectCleanup.sql")));
            _progressLog.Received().Warn(Arg.Is<string>(s => s.Contains("1 invalid script(s)")));

            CleanUp();
        }
    }

    [Test]
    public void GenerateInvalidObjectCleanupScript_PostgreSQL_CorrectQuoting()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.PostgreSQL);
            tongs._invalidScripts.Add(("public.bad_view.sql", "Error", ScriptObjectType.Views));

            _directoryWrapper.Exists(Arg.Any<string>()).Returns(false);

            tongs.GenerateInvalidObjectCleanupScript();

            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.Contains("_InvalidObjectCleanup.sql")),
                Arg.Is<string>(s => s.Contains("\"public\".\"bad_view\"")));

            CleanUp();
        }
    }

    [Test]
    public void GenerateInvalidObjectCleanupScript_MySQL_CorrectQuoting()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var tongs = CreateTongs(Platform.MySQL);
            tongs._invalidScripts.Add(("bad_func.sql", "Error", ScriptObjectType.Functions));

            _directoryWrapper.Exists(Arg.Any<string>()).Returns(false);

            tongs.GenerateInvalidObjectCleanupScript();

            _fileWrapper.Received().WriteAllText(
                Arg.Is<string>(s => s.Contains("_InvalidObjectCleanup.sql")),
                Arg.Is<string>(s => s.Contains("`bad_func`")));

            CleanUp();
        }
    }
}

[TestFixture]
public class LoadValidationSettingsTests
{
    private ILog _progressLog;
    private IFile _fileWrapper;
    private IDirectory _directoryWrapper;
    private IDbConnectionFactory _connectionFactory;
    private IDbConnection _connection;
    private IDbCommand _command;

    [SetUp]
    public void SetUp()
    {
        _progressLog = Substitute.For<ILog>();
        _fileWrapper = Substitute.For<IFile>();
        _directoryWrapper = Substitute.For<IDirectory>();
        _connectionFactory = Substitute.For<IDbConnectionFactory>();
        _connection = Substitute.For<IDbConnection>();
        _command = Substitute.For<IDbCommand>();

        _connectionFactory.GetDbConnection(Arg.Any<string>()).Returns(_connection);
        _connection.CreateCommand().Returns(_command);
        _directoryWrapper.Exists(Arg.Any<string>()).Returns(true);
        _directoryWrapper.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns(Array.Empty<string>());
        _fileWrapper.Exists(Arg.Any<string>()).Returns(false);
    }

    [TearDown]
    public void TearDown()
    {
        _command?.Dispose();
        _connection?.Dispose();
    }

    private void RegisterConfig(Dictionary<string, string> overrides = null)
    {
        var configValues = new Dictionary<string, string>
        {
            ["Source:Server"] = "localhost",
            ["Source:Database"] = "testdb",
            ["Source:User"] = "testuser",
            ["Source:Password"] = "testpass",
            ["Product:Path"] = Path.GetTempPath(),
            ["Product:Name"] = "TestProduct",
            ["Template:Name"] = "TestTemplate",
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
            ["ShouldCast:IndexedViews"] = "false"
        };

        if (overrides != null)
            foreach (var kv in overrides)
                configValues[kv.Key] = kv.Value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        FactoryContainer.Register<IConfigurationRoot>(config);
        FactoryContainer.Register(Substitute.For<IEnvironment>());
        FactoryContainer.Register(_fileWrapper);
        FactoryContainer.Register(_directoryWrapper);
        FactoryContainer.Register(_connectionFactory);
        LogFactory.Register("ProgressLog", _progressLog);
        LogFactory.Register("ErrorLog", Substitute.For<ILog>());

        _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(true);
        _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Product.json"))).Returns(
            "{\"Name\":\"TestProduct\",\"Platform\":\"SqlServer\",\"TemplateOrder\":[\"TestTemplate\"],\"ScriptTokens\":{},\"ScriptFolders\":[]}");
        _fileWrapper.Exists(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(true);
        _fileWrapper.ReadAllText(Arg.Is<string>(s => s.Contains("Template.json"))).Returns(
            "{\"Name\":\"TestTemplate\",\"ScriptFolders\":[],\"ScriptTokens\":{}}");
    }

    private void CleanUp()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void CastTemplate_ValidateScripts_DefaultsFalse()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            RegisterConfig();

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            // No validation should occur (no calls to BeginTransaction for validation)
            Assert.That(tongs._invalidScripts, Is.Empty);

            CleanUp();
        }
    }

    [Test]
    public void CastTemplate_ValidateScriptsTrue_SettingParsed()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            RegisterConfig(new Dictionary<string, string>
            {
                ["ShouldCast:ValidateScripts"] = "true",
                ["ShouldCast:Views"] = "true"
            });

            var emptyReader = Substitute.For<IDataReader>();
            emptyReader.Read().Returns(false);
            _command.StubReaders(emptyReader);

            var tongs = new SchemaTongs(Platform.SqlServer);
            tongs.CastTemplate();

            // With no objects extracted, no validations, but setting was parsed
            Assert.That(tongs._invalidScripts, Is.Empty);

            CleanUp();
        }
    }
}
