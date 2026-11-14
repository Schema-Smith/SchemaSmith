// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data;
using NSubstitute;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

/// <summary>
/// On a SQL Server connection that reports user errors as messages, a statement that failed returns normally. The wrapper
/// turns the error the message handler captured during the statement into the exception, and does not blame a statement
/// for an error an earlier one left behind.
/// </summary>
[TestFixture]
public class ServerErrorCheckingCommandTests
{
    private Exception _captured;

    private Exception Take()
    {
        var error = _captured;
        _captured = null;
        return error;
    }

    private IDbCommand Failing(Func<IDbCommand, object> run)
    {
        var inner = Substitute.For<IDbCommand>();
        inner.ExecuteNonQuery().Returns(_ => { _captured = new InvalidOperationException("Msg 2714: There is already an object named 'app'"); return -1; });
        inner.ExecuteScalar().Returns(_ => { _captured = new InvalidOperationException("Msg 8134: Divide by zero error encountered."); return null; });
        inner.ExecuteReader(Arg.Any<CommandBehavior>()).Returns(_ => { _captured = new InvalidOperationException("Msg 208: Invalid object name"); return Substitute.For<IDataReader>(); });
        var wrapped = new ServerErrorCheckingCommand(inner, Take);
        run(wrapped);
        return wrapped;
    }

    [Test]
    public void AnErrorDuringTheStatement_Throws_WithTheServersText()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => Failing(c => c.ExecuteNonQuery()), Throws.InvalidOperationException.With.Message.Contains("There is already an object named"));
            Assert.That(() => Failing(c => c.ExecuteScalar()), Throws.InvalidOperationException.With.Message.Contains("Divide by zero"));
            Assert.That(() => Failing(c => c.ExecuteReader()), Throws.InvalidOperationException.With.Message.Contains("Invalid object name"));
        });
    }

    [Test]
    public void AnErrorAnEarlierStatementLeft_IsNotThisOnes()
    {
        var inner = Substitute.For<IDbCommand>();
        inner.ExecuteNonQuery().Returns(1);
        var wrapped = new ServerErrorCheckingCommand(inner, Take);
        _captured = new InvalidOperationException("left over");

        Assert.That(wrapped.ExecuteNonQuery(), Is.EqualTo(1));
    }
}
