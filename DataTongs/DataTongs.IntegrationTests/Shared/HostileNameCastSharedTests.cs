// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using DataTongs.IntegrationTests.Support;
using log4net;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Delivery;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.IntegrationTests.Shared;

/// <summary>
/// A data cast of a table whose schema, name and columns hold every character a name can carry that SQL treats specially:
/// the run names it in its settings, extracts it, and writes a script that puts the extracted rows back.
/// </summary>
public abstract class HostileNameCastSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }
    // The database the cast reads, and a connection string to it.
    protected abstract string Database { get; }
    protected abstract string DatabaseConnectionString { get; }
    // The schema the table lives in; empty where the engine has none.
    protected abstract string Schema { get; }
    protected abstract string H { get; }
    protected abstract string Quote(string name);
    // How a user names the table in Tables:N:Name.
    protected virtual string SettingsTableName => string.IsNullOrEmpty(Schema) ? Quote(Table) : $"{Quote(Schema)}.{Quote(Table)}";
    protected virtual void CreateSchema(IDbCommand command) { }
    protected virtual void DropSchema(IDbCommand command) { }
    protected virtual IEnumerable<string> Batches(string script) => new[] { script };

    private string Table => "t" + H;
    private string Key => "k" + H;
    private string Value => "v" + H;
    private string Target => string.IsNullOrEmpty(Schema) ? Quote(Table) : $"{Quote(Schema)}.{Quote(Table)}";
    private IDbConnection _connection = null!;
    private string _outputDir = null!;

    [SetUp]
    public void CreateTheTable()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(DatabaseConnectionString);
        _connection.Open();
        _outputDir = Path.Join(Path.GetTempPath(), $"DataTongsHostile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputDir);
        using var command = _connection.CreateCommand();
        CreateSchema(command);
        Exec($"CREATE TABLE {Target} ({Quote(Key)} INT NOT NULL PRIMARY KEY, {Quote(Value)} VARCHAR(40) NULL)");
        Exec($"INSERT INTO {Target} ({Quote(Key)}, {Quote(Value)}) VALUES (1, 'one'), (2, 'two'), (3, 'three')");
    }

    [TearDown]
    public void DropTheTable()
    {
        try
        {
            Exec($"DROP TABLE IF EXISTS {Target}");
            using var command = _connection.CreateCommand();
            DropSchema(command);
        }
        finally
        {
            _connection.Dispose();
            if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, true);
        }
    }

    [TestCase("Json")]
    [TestCase("Xml")]
    public void TheCastScript_PutsTheExtractedRowsBack(string encoding)
    {
        var errors = new List<string>();
        var progress = new List<string>();
        var errorLog = Substitute.For<ILog>();
        errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        errorLog.When(l => l.Error(Arg.Any<object>(), Arg.Any<Exception>())).Do(ci => errors.Add($"{ci.ArgAt<object>(0)} {ci.ArgAt<Exception>(1)?.Message}"));
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Unregister<IMergeScriptHelper>();
            LogFactory.Register("ErrorLog", errorLog);
            var progressLog = Substitute.For<ILog>();
            progressLog.When(l => l.Info(Arg.Any<object>())).Do(ci => progress.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
            LogFactory.Register("ProgressLog", progressLog);
            ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            using var scope = IsolatedConfigScope.Create();
            var config = scope.Config;
            config["Source:Server"] = config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config[$"{ConfigPrefix}:Port"];
            config["Source:User"] = config[$"{ConfigPrefix}:User"];
            config["Source:Password"] = config[$"{ConfigPrefix}:Password"];
            config["Source:Database"] = Database;
            foreach (var prop in global::Schema.DataAccess.ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = SettingsTableName;
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = "true";
            config["ShouldCast:TokenizeScripts"] = "false";
            config["ShouldCast:MergeUpdate"] = "true";
            config["ShouldCast:MergeDelete"] = "true";
            config["ShouldCast:DeliveryEncoding"] = encoding;
            config["ContentPath"] = _outputDir;
            config["ScriptPath"] = _outputDir;
            try
            {
                new global::DataTongs.DataTongs(Platform).CastData();
            }
            finally
            {
                LogFactory.Clear();
            }
        }

        var scripts = Directory.GetFiles(_outputDir, "*.sql");
        Assert.That(errors, Is.Empty, "the cast reported errors");
        Assert.That(scripts, Has.Length.EqualTo(1), "one script for the one table; the run wrote: "
            + string.Join(", ", Directory.GetFiles(_outputDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName))
            + " / progress: " + string.Join(" / ", progress));

        Exec($"DELETE FROM {Target} WHERE {Quote(Key)} = 2");
        Exec($"UPDATE {Target} SET {Quote(Value)} = 'changed' WHERE {Quote(Key)} = 1");
        Exec($"INSERT INTO {Target} ({Quote(Key)}, {Quote(Value)}) VALUES (9, 'stray')");
        foreach (var batch in Batches(File.ReadAllText(scripts[0])))
            Exec(batch);

        Assert.That(Rows(), Is.EqualTo("1:one|2:two|3:three"), "the rows as they were extracted");
    }

    private string Rows()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {Quote(Key)}, {Quote(Value)} FROM {Target} ORDER BY {Quote(Key)}";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add($"{reader.GetValue(0)}:{reader.GetValue(1)}");
        return string.Join("|", rows);
    }

    protected void Exec(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
