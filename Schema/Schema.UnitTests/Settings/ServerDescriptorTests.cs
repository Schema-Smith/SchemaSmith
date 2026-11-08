// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.Settings;
using Schema.Settings;

namespace Schema.UnitTests.Settings;

[TestFixture]
public class ServerDescriptorTests
{
    private sealed class ReadOnlyVariableException() : DbException("Variable 'back_log' is a read only variable");

    private static (IDbCommand Cmd, List<string> Sql, Dictionary<string, object> Params) Recorder(Func<string, Exception> failFor = null)
    {
        var sql = new List<string>();
        var parms = new Dictionary<string, object>();
        var cmd = Substitute.For<IDbCommand>();
        cmd.Parameters.Returns(Substitute.For<IDataParameterCollection>());
        cmd.CreateParameter().Returns(_ =>
        {
            var p = Substitute.For<IDbDataParameter>();
            p.When(x => x.Value = Arg.Any<object>()).Do(ci => parms[p.ParameterName] = ci.Arg<object>());
            return p;
        });
        cmd.When(c => c.ExecuteNonQuery()).Do(_ =>
        {
            sql.Add(cmd.CommandText);
            if (failFor?.Invoke(cmd.CommandText) is { } e) throw e;
        });
        return (cmd, sql, parms);
    }

    [TestCase(Platform.SqlServer, "Configuration", typeof(SqlServerConfigurationDescriptor))]
    [TestCase(Platform.SqlServer, "TraceFlag", typeof(SqlServerTraceFlagDescriptor))]
    [TestCase(Platform.PostgreSQL, "Parameter", typeof(PostgreSqlServerParameterDescriptor))]
    [TestCase(Platform.MySQL, "Variable", typeof(MySqlVariableDescriptor))]
    [TestCase(Platform.MariaDb, "Variable", typeof(MySqlVariableDescriptor))]
    public void Factory_PicksTheServerDescriptor(Platform platform, string kind, Type expected)
    {
        Assert.That(SettingDescriptors.For(platform, SettingScope.Server, kind, 16), Is.InstanceOf(expected));
    }

    // TargetVersionDetector's comparable is the major on SQL Server and PostgreSQL, and major * 100 + minor on the MySQL family.
    [TestCase(Platform.SqlServer, 16, 16)]
    [TestCase(Platform.PostgreSQL, 12, 12)]
    [TestCase(Platform.MySQL, 807, 8)]
    [TestCase(Platform.MySQL, 507, 5)]
    [TestCase(Platform.MariaDb, 1104, 11)]
    public void MajorVersion_FromTheDetectorsComparable(Platform platform, int comparable, int major)
    {
        Assert.That(SettingDescriptors.MajorVersion(platform, comparable), Is.EqualTo(major));
    }

    [Test]
    public void SqlServerConfiguration_PassesNameAndValueAsParameters_AndRestoresAdvancedOptions()
    {
        var (cmd, sql, parms) = Recorder();

        new SqlServerConfigurationDescriptor().Apply(cmd, "cost threshold for parallelism", "50", null);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Has.Count.EqualTo(1));
            Assert.That(sql[0], Does.Contain("EXEC sp_configure @name, @value; RECONFIGURE;"));
            Assert.That(sql[0], Does.Contain("EXEC sp_configure 'show advanced options', 0; RECONFIGURE;"), "advanced options are put back off");
            Assert.That(sql[0], Does.Contain("BEGIN CATCH"), "even when the change fails");
            Assert.That(parms["@name"], Is.EqualTo("cost threshold for parallelism"));
            Assert.That(parms["@value"], Is.EqualTo(50));
        });
    }

    // THROW arrived in SQL Server 2012; the floor is 2008.
    [Test]
    public void SqlServerConfiguration_ReRaisesWithoutThrow()
    {
        var (cmd, sql, _) = Recorder();
        new SqlServerConfigurationDescriptor().Apply(cmd, "cost threshold for parallelism", "50", null);
        Assert.That(sql[0], Does.Not.Contain("THROW").And.Contain("RAISERROR"));
    }

    [TestCase("STRICT_TRANS_TABLES,NO_ZERO_DATE", "NO_ZERO_DATE,STRICT_TRANS_TABLES", true)]
    [TestCase("STRICT_TRANS_TABLES", "STRICT_TRANS_TABLES,NO_ZERO_DATE", false)]
    // optimizer_switch takes flag=value pairs and SET merges them into the rest: the declared flags are what must hold.
    [TestCase("index_merge=off", "index_merge=off,index_merge_union=on,mrr=on", true)]
    [TestCase("mrr=on, index_merge=off", "index_merge=off,index_merge_union=on,mrr=on", true)]
    [TestCase("index_merge=off", "index_merge=on,index_merge_union=on,mrr=on", false)]
    [TestCase("no_such_flag=on", "index_merge=on,mrr=on", false)]
    public void MySqlVariable_AListComparesAsASet(string declared, string current, bool equal)
    {
        Assert.That(new MySqlVariableDescriptor(Platform.MySQL, 8).Equivalent(declared, new SettingReading(true, current, current, false, null)), Is.EqualTo(equal));
    }

    [Test]
    public void SqlServerConfiguration_RefusesANonNumber()
    {
        var (cmd, sql, _) = Recorder();
        Assert.That(() => new SqlServerConfigurationDescriptor().Apply(cmd, "max degree of parallelism", "eight", null), Throws.InstanceOf<SettingValueException>());
        Assert.That(sql, Is.Empty);
    }

    [TestCase("ON", "DBCC TRACEON(1117, -1)")]
    [TestCase("off", "DBCC TRACEOFF(1117, -1)")]
    [TestCase("1", "DBCC TRACEON(1117, -1)")]
    public void SqlServerTraceFlag_TurnsTheFlagOnOrOffGlobally(string value, string expected)
    {
        var (cmd, sql, _) = Recorder();
        new SqlServerTraceFlagDescriptor().Apply(cmd, "1117", value, null);
        Assert.That(sql, Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void SqlServerTraceFlag_DoesNotSurviveARestart_AndSaysHowToKeepIt()
    {
        var descriptor = new SqlServerTraceFlagDescriptor();
        Assert.That(descriptor.PersistsAcrossRestart, Is.False);
        Assert.That(descriptor.RevertAdvice("1117"), Is.EqualTo("add -T1117 to the startup parameters to keep it"));
    }

    [Test]
    public void SqlServerTraceFlag_ANameThatIsNotANumber_IsNotRead()
    {
        var reading = new SqlServerTraceFlagDescriptor().Read(Substitute.For<IDbCommand>(), "abc", null);
        Assert.That(reading.UnfixableReason, Is.EqualTo("a trace flag is named by its number"));
    }

    [Test]
    public void PostgreSqlServerParameter_AltersTheSystem_AndReloads()
    {
        var (cmd, sql, _) = Recorder();
        new PostgreSqlServerParameterDescriptor().Apply(cmd, "log_min_duration_statement", "250ms", null);
        Assert.That(sql, Is.EqualTo(new[] { "ALTER SYSTEM SET \"log_min_duration_statement\" = '250ms'", "SELECT pg_reload_conf()" }));
    }

    [TestCase(8, "SET PERSIST max_connections = 180", true)]
    [TestCase(5, "SET GLOBAL max_connections = 180", false)]
    public void MySqlVariable_PersistsWhereTheServerCan(int major, string expected, bool persists)
    {
        var (cmd, sql, _) = Recorder();
        var descriptor = new MySqlVariableDescriptor(Platform.MySQL, major);
        descriptor.Apply(cmd, "max_connections", "180", null);
        Assert.That(sql, Is.EqualTo(new[] { expected }));
        Assert.That(descriptor.PersistsAcrossRestart, Is.EqualTo(persists));
    }

    [Test]
    public void MariaDb_NeverPersists()
    {
        var (cmd, sql, _) = Recorder();
        var descriptor = new MySqlVariableDescriptor(Platform.MariaDb, 11);
        descriptor.Apply(cmd, "max_connections", "180", null);
        Assert.That(sql, Is.EqualTo(new[] { "SET GLOBAL max_connections = 180" }));
        Assert.That(descriptor.PersistsAcrossRestart, Is.False);
    }

    [TestCase("ON", "SET GLOBAL slow_query_log = ON")]
    [TestCase("utf8mb4_bin", "SET GLOBAL collation_server = 'utf8mb4_bin'")]
    [TestCase("a'b\\c", "SET GLOBAL collation_server = 'a''b\\\\c'")]
    public void MySqlVariable_ValueShapes(string value, string expected)
    {
        var (cmd, sql, _) = Recorder();
        var name = value == "ON" ? "slow_query_log" : "collation_server";
        new MySqlVariableDescriptor(Platform.MariaDb, 10).Apply(cmd, name, value, null);
        Assert.That(sql, Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void MySql8_AReadOnlyVariable_IsPersistedForTheNextStart()
    {
        var (cmd, sql, _) = Recorder(text => text.StartsWith("SET PERSIST ") ? new ReadOnlyVariableException() : null);
        new MySqlVariableDescriptor(Platform.MySQL, 8).Apply(cmd, "back_log", "100", null);
        Assert.That(sql, Is.EqualTo(new[] { "SET PERSIST back_log = 100", "SET PERSIST_ONLY back_log = 100" }));
    }

    [Test]
    public void MariaDb_AReadOnlyVariable_CannotBeFixedHere()
    {
        var (cmd, _, _) = Recorder(_ => new ReadOnlyVariableException());
        Assert.That(() => new MySqlVariableDescriptor(Platform.MariaDb, 11).Apply(cmd, "back_log", "100", null),
            Throws.InstanceOf<SettingUnfixableException>().With.Message.EqualTo("read-only variable; set it in the server's option file"));
    }

    [Test]
    public void MySqlVariable_ANameThatIsNotAVariableName_IsNotSpliced()
    {
        var (cmd, sql, _) = Recorder();
        Assert.That(() => new MySqlVariableDescriptor(Platform.MySQL, 8).Apply(cmd, "x; DROP", "1", null), Throws.InstanceOf<SettingValueException>());
        Assert.That(sql, Is.Empty);
    }
}
