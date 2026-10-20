// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using NSubstitute;

namespace SchemaQuench.UnitTests;

public class PostgreSqlOnlineWorkTests
{
    private sealed class BuildFailedException() : DbException("could not create unique index");

    private const string Build = "CREATE INDEX CONCURRENTLY \"ix\" ON \"public\".\"t\" (a);";
    private const string Cleanup = "\"public\".\"ix\"";
    private const string FollowUp = "INSERT INTO \"SchemaSmith\".\"ChangeAudit\" VALUES (1);";
    private const string DropInvalid = "DROP INDEX CONCURRENTLY IF EXISTS \"public\".\"ix\"";

    // A command whose queue holds one build, and which fails that build the given number of times.
    private static (IDbCommand Command, List<string> Executed) Queue(int failures, string cleanup = Cleanup, bool leavesInvalidIndex = true)
    {
        var command = Substitute.For<IDbCommand>();
        command.Parameters.Returns(Substitute.For<IDataParameterCollection>());
        command.ExecuteScalar().Returns(_ => !command.CommandText.Contains("indisvalid") || leavesInvalidIndex);
        var reader = Substitute.For<IDataReader>();
        reader.Read().Returns(true, false);
        reader.GetString(0).Returns("Building Index public.t.ix concurrently");
        reader.GetString(1).Returns(Build);
        reader.IsDBNull(2).Returns(cleanup == null);
        reader.GetString(2).Returns(cleanup);
        reader.IsDBNull(3).Returns(false);
        reader.GetString(3).Returns(FollowUp);
        command.ExecuteReader().Returns(reader);

        var executed = new List<string>();
        var left = failures;
        command.When(c => c.ExecuteNonQuery()).Do(_ =>
        {
            executed.Add(command.CommandText);
            if (command.CommandText == Build && left-- > 0) throw new BuildFailedException();
        });
        return (command, executed);
    }

    [Test]
    public void ABuildThatFailsOnce_IsRetriedAfterItsInvalidIndexIsDropped_ThenFollowedUp()
    {
        var (command, executed) = Queue(failures: 1);
        var log = new List<string>();

        PostgreSqlOnlineWork.Run(command, log.Add, _ => { });

        Assert.Multiple(() =>
        {
            Assert.That(executed, Is.EqualTo(new[] { Build, DropInvalid, Build, FollowUp, "DROP TABLE IF EXISTS pg_temp.temp_deferred_online_work" }));
            Assert.That(log, Has.Some.Contains("left \"public\".\"ix\" invalid; dropping it and building once more"));
        });
    }

    [Test]
    public void ABuildThatFailsTwice_FailsTheDeploy_WithItsInvalidIndexDropped_AndNoFollowUp()
    {
        var (command, executed) = Queue(failures: 2);

        Assert.Throws<BuildFailedException>(() => PostgreSqlOnlineWork.Run(command, _ => { }, _ => { }));
        Assert.That(executed, Is.EqualTo(new[] { Build, DropInvalid, Build, DropInvalid }));
    }

    [Test]
    public void ABuildWhoseNameIsTakenByAValidIndex_FailsAtOnce_AndLeavesThatIndexAlone()
    {
        var (command, executed) = Queue(failures: 1, leavesInvalidIndex: false);

        Assert.Throws<BuildFailedException>(() => PostgreSqlOnlineWork.Run(command, _ => { }, _ => { }));
        Assert.That(executed, Is.EqualTo(new[] { Build }), "a valid index of the same name belongs to another table; dropping it loses it");
    }

    [Test]
    public void AStatementWithNoIndexToCleanUp_FailsAtOnce()
    {
        var (command, executed) = Queue(failures: 1, cleanup: null);

        Assert.Throws<BuildFailedException>(() => PostgreSqlOnlineWork.Run(command, _ => { }, _ => { }));
        Assert.That(executed, Is.EqualTo(new[] { Build }), "a validation the rows fail would fail again; it is not retried");
    }

    [Test]
    public void NoQueue_RunsNothing()
    {
        var command = Substitute.For<IDbCommand>();
        command.Parameters.Returns(Substitute.For<IDataParameterCollection>());
        command.ExecuteScalar().Returns(false);

        PostgreSqlOnlineWork.Run(command, _ => { }, _ => { });

        command.DidNotReceive().ExecuteNonQuery();
        command.DidNotReceive().ExecuteReader();
    }
}
