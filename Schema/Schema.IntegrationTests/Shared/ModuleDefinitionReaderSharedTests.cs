// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// The unchanged-module stamp skips a module script whose rendering matches the one recorded when it last ran, so the
/// rendering has to change with anything a redeploy would put back. A trigger's enabled state is not in its definition
/// on SQL Server or PostgreSQL, so disabling it out of band must still read as a change; the MySQL family has no such
/// state, and a changed body is the change there. An object that is gone reads as null, which the caller treats as
/// changed.
/// </summary>
public abstract class ModuleDefinitionReaderSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConnectionString { get; }
    protected abstract string Setup { get; }
    protected abstract string ChangeTrigger { get; }
    protected abstract string Teardown { get; }
    protected abstract ModuleScriptIdentity Trigger { get; }

    private IDbConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(ConnectionString);
        _connection.Open();
        Exec(Teardown);
        Exec(Setup);
    }

    [TearDown]
    public void TearDown()
    {
        Exec(Teardown);
        _connection.Dispose();
    }

    private void Exec(string sql)
    {
        foreach (var statement in sql.Split("\n--\n"))
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }
    }

    private string Read(ModuleScriptIdentity identity)
    {
        using var cmd = _connection.CreateCommand();
        return ModuleDefinitionReader.Read(cmd, Platform, identity);
    }

    [Test]
    public void AnUnchangedTrigger_ReadsTheSameTwice()
    {
        var first = Read(Trigger);
        Assert.That(first, Is.Not.Null.And.Not.Empty, "the trigger is rendered");
        Assert.That(Read(Trigger), Is.EqualTo(first));
    }

    [Test]
    public void AChangedTrigger_ReadsDifferently()
    {
        var before = Read(Trigger);
        Exec(ChangeTrigger);
        Assert.That(Read(Trigger), Is.Not.EqualTo(before), "a redeploy would put it back, so the stamp must not skip it");
    }

    [Test]
    public void AMissingTrigger_ReadsNull()
    {
        Exec(Teardown);
        Assert.That(Read(Trigger), Is.Null);
    }
}
