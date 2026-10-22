// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.SqlServer;

/// <summary>
/// An Always Encrypted column needs its column encryption key, and the key its column master key, before the table can
/// be created. Neither is modelled, so a package of such a table failed on an empty database. SchemaTongs now writes
/// them to a generated prerequisite script that runs ahead of the tables and creates only what is missing.
/// </summary>
[Category("SqlServer")]
[NonParallelizable]
public class AlwaysEncryptedKeyExtractionTests
{
    private const string ProductName = "AeKeysProduct";
    private const string TemplateName = "Main";
    private const string CekValue1 = "0x016E000001630075007200720065006E0074007500730065007200";
    private const string CekValue2 = "0x016E00000163007500720072AABBCC";

    private readonly List<string> _dbs = [];
    private string _db = "";
    private string _masterConnectionString = "";
    private string _tempProductPath = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "SqlServer:ConnectionProperties");
        _masterConnectionString = ConnectionString.Build(Platform.SqlServer, config["SqlServer:Server"], "master",
            config["SqlServer:User"], config["SqlServer:Password"], config["SqlServer:Port"], connProps);
        _db = NewDatabase("TongsAeKeys");
        OnDb(_db, cmd =>
        {
            Run(cmd, "CREATE COLUMN MASTER KEY [CMK_O'Brien] WITH (KEY_STORE_PROVIDER_NAME = N'MSSQL_CERTIFICATE_STORE', KEY_PATH = N'CurrentUser/My/0123456789ABCDEF0123456789ABCDEF01234567')");
            Run(cmd, "CREATE COLUMN MASTER KEY [CMK_Next] WITH (KEY_STORE_PROVIDER_NAME = N'AZURE_KEY_VAULT', KEY_PATH = N'https://vault.example/keys/next/1')");
            Run(cmd, $"CREATE COLUMN ENCRYPTION KEY [CEK_Main] WITH VALUES (COLUMN_MASTER_KEY = [CMK_O'Brien], ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = {CekValue1})");
            Run(cmd, $"ALTER COLUMN ENCRYPTION KEY [CEK_Main] ADD VALUE (COLUMN_MASTER_KEY = [CMK_Next], ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = {CekValue2})");
            Run(cmd, "CREATE TABLE dbo.Secret (Id INT NOT NULL CONSTRAINT PK_Secret PRIMARY KEY, Ssn CHAR(11) COLLATE Latin1_General_BIN2 " +
                     "ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = [CEK_Main], ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NULL)");
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            foreach (var db in _dbs) Schema.IntegrationTests.SqlServer.TestDatabaseDrop.Drop(cmd, db);
        }
        catch (DbException) { /* teardown must not mask an assertion */ }

        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); } catch (IOException) { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void TheKeysAreWrittenToAPrerequisiteScript_AndTheExtractedTableDeploysToAnEmptyDatabase()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsAeKeys_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);
        Extract();

        var templateDir = Path.Join(_tempProductPath, "Templates", TemplateName);
        var scriptFile = Path.Join(templateDir, "Prerequisites", "AlwaysEncryptedKeys.sql");
        Assert.That(File.Exists(scriptFile), Is.True, "the keys are written to the prerequisite script");
        var script = File.ReadAllText(scriptFile);
        Assert.Multiple(() =>
        {
            Assert.That(GeneratedScript.IsGenerated(script) && !GeneratedScript.IsHandEdited(script), Is.True, "stamped as generated");
            Assert.That(FirstFolder(templateDir), Is.EqualTo("Prerequisites:Objects"), "the template runs it first, in the pass ahead of the tables");
        });

        // An empty database: the script creates the keys, a second run changes nothing, and the extracted table deploys.
        var target = NewDatabase("TongsAeTarget");
        OnDb(target, cmd =>
        {
            Run(cmd, script);
            Run(cmd, script);
            Assert.That(Keys(cmd), Is.EqualTo(Keys(_db)), "the same keys, with every value, as the source");

            ForgeKindler.KindleTheForge(cmd, Platform.SqlServer);
            var table = File.ReadAllText(Path.Join(templateDir, "Tables", "dbo.Secret.json"));
            cmd.CommandText = $"EXEC SchemaSmith.TableQuench @ProductName = '{ProductName}', @TableDefinitions = '[{table.Replace("'", "''")}]', @DropTablesRemovedFromProduct = 0";
            cmd.CommandTimeout = 300;
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT k.[name] COLLATE DATABASE_DEFAULT + ':' + c.encryption_type_desc COLLATE DATABASE_DEFAULT FROM sys.columns c JOIN sys.column_encryption_keys k ON k.column_encryption_key_id = c.column_encryption_key_id WHERE c.[object_id] = OBJECT_ID('dbo.Secret')";
            Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("CEK_Main:DETERMINISTIC"), "the encrypted column is created with its key");
        });

        // A template that lists its folders without Prerequisites gets it back, first, on the next extraction.
        var templateFile = Path.Join(templateDir, "Template.json");
        var json = JObject.Parse(File.ReadAllText(templateFile));
        json["ScriptFolders"]!.Where(f => (string)f["FolderPath"] == "Prerequisites").ToList().ForEach(f => f.Remove());
        File.WriteAllText(templateFile, json.ToString());
        Assert.That(FirstFolder(templateDir), Does.Not.StartWith("Prerequisites"), "precondition");
        Extract();
        Assert.That(FirstFolder(templateDir), Is.EqualTo("Prerequisites:Objects"));
    }

    private void Extract()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            new global::SchemaTongs.SchemaTongs(Platform.SqlServer).CastTemplate();
        }
    }

    private static string FirstFolder(string templateDir)
    {
        var first = JObject.Parse(File.ReadAllText(Path.Join(templateDir, "Template.json")))["ScriptFolders"]!.First();
        return $"{first["FolderPath"]}:{first["QuenchSlot"] ?? "Before"}";
    }

    private string Keys(string db)
    {
        var keys = "";
        OnDb(db, cmd => keys = Keys(cmd));
        return keys;
    }

    private static string Keys(IDbCommand cmd)
    {
        cmd.CommandText = """
            SELECT STRING_AGG(CAST(x AS NVARCHAR(MAX)), ' | ') WITHIN GROUP (ORDER BY x) FROM (
              SELECT x = CAST(m.[name] AS NVARCHAR(400)) COLLATE DATABASE_DEFAULT + '/' + CAST(m.key_store_provider_name AS NVARCHAR(400)) COLLATE DATABASE_DEFAULT
                         + '/' + CAST(m.key_path AS NVARCHAR(4000)) COLLATE DATABASE_DEFAULT
                FROM sys.column_master_keys m
              UNION ALL
              SELECT CAST(k.[name] AS NVARCHAR(400)) COLLATE DATABASE_DEFAULT + '<' + CAST(m.[name] AS NVARCHAR(400)) COLLATE DATABASE_DEFAULT
                     + '/' + CAST(v.encryption_algorithm_name AS NVARCHAR(400)) COLLATE DATABASE_DEFAULT + '/' + CONVERT(NVARCHAR(400), CONVERT(VARCHAR(400), v.encrypted_value, 1)) COLLATE DATABASE_DEFAULT
                FROM sys.column_encryption_keys k
                JOIN sys.column_encryption_key_values v ON v.column_encryption_key_id = k.column_encryption_key_id
                JOIN sys.column_master_keys m ON m.column_master_key_id = v.column_master_key_id) s
            """;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private string NewDatabase(string prefix)
    {
        var db = $"{prefix}_{Guid.NewGuid():N}"[..30];
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        Run(cmd, $"CREATE DATABASE [{db}]");
        _dbs.Add(db);
        return db;
    }

    private static void Run(IDbCommand cmd, string sql)
    {
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        cmd.ExecuteNonQuery();
    }

    private void OnDb(string db, Action<IDbCommand> act)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_masterConnectionString);
        conn.Open();
        conn.ChangeDatabase(db);
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        act(cmd);
    }

    private IConfigurationRoot BuildConfig()
    {
        var root = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = root["SqlServer:Server"],
            ["Source:Port"] = root["SqlServer:Port"],
            ["Source:User"] = root["SqlServer:User"],
            ["Source:Password"] = root["SqlServer:Password"],
            ["Source:Database"] = _db,
            ["Target:Server"] = root["SqlServer:Server"],
            ["Target:Port"] = root["SqlServer:Port"],
            ["Target:User"] = root["SqlServer:User"],
            ["Target:Password"] = root["SqlServer:Password"],
            ["ScriptTokens:MainDB"] = _db,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = ProductName,
            ["Template:Name"] = TemplateName,
            ["ShouldCast:Tables"] = "true",
            ["ShouldCast:Views"] = "false",
            ["ShouldCast:Procedures"] = "false",
            ["ShouldCast:Functions"] = "false",
            ["ShouldCast:UserDefinedTypes"] = "false",
            ["ShouldCast:TableTriggers"] = "false",
            ["ShouldCast:Schemas"] = "false",
            ["ShouldCast:IndexedViews"] = "false",
            ["ShouldCast:Sequences"] = "false",
            ["ShouldCast:Synonyms"] = "false",
            ["ShouldCast:XmlSchemaCollections"] = "false",
            ["ShouldCast:FullTextCatalogs"] = "false",
            ["ShouldCast:FullTextStopLists"] = "false",
        };
        foreach (var kv in ConnectionString.ReadProperties(root, "SqlServer:ConnectionProperties"))
        {
            values[$"Source:ConnectionProperties:{kv.Key}"] = kv.Value;
            values[$"Target:ConnectionProperties:{kv.Key}"] = kv.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
