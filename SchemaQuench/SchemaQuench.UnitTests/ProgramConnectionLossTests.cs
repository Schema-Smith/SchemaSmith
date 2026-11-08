// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Net.Sockets;
using NUnit.Framework;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

/// <summary>
/// A run that failed to reach a server says which one and as whom ("Unable to connect to X with user Y"), in a
/// RunFailedException that may carry the socket error inside it. The last-resort connection-lost handler, whose message
/// cannot name the server, must not take that one over.
/// </summary>
[TestFixture]
public class ProgramConnectionLossTests
{
    [Test]
    public void ARunFailureThatNamesTheServer_IsNotTheGenericConnectionLoss() =>
        Assert.That(Program.IsUnexplainedConnectionLoss(new RunFailedException("Unable to connect to SQL Server at secondary with user sa", new SocketException(10061))), Is.False);

    [Test]
    public void ADropNothingExplained_IsTheGenericConnectionLoss() =>
        Assert.That(Program.IsUnexplainedConnectionLoss(new InvalidOperationException("wrapped", new SocketException(10054))), Is.True);
}
