// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.DataAccess;

namespace Schema.UnitTests.DataAccess;

[TestFixture]
public class BatchSplitterTests
{
    [Test]
    public void Split_NoDelimiterCommand_ReturnsSingleBatch()
    {
        var script = "SELECT 1;";
        var batches = BatchSplitter.Split(script);

        Assert.That(batches, Has.Count.EqualTo(1));
        Assert.That(batches[0], Is.EqualTo("SELECT 1;"));
    }

    [Test]
    public void Split_WithDelimiterCommand_SplitsCorrectly()
    {
        var script = "DELIMITER //\nCREATE PROCEDURE p1()\nBEGIN\n  SELECT 1;\nEND//\nDELIMITER ;\nSELECT 2;";
        var batches = BatchSplitter.Split(script);

        Assert.That(batches, Has.Count.EqualTo(2));
        Assert.That(batches[0], Does.Contain("CREATE PROCEDURE"));
        Assert.That(batches[1], Does.Contain("SELECT 2;"));
    }

    [Test]
    public void Split_DelimiterChangedBack_HandlesSemicolonBatches()
    {
        var script = "DELIMITER //\nBODY//\nDELIMITER ;\nA;\nB;";
        var batches = BatchSplitter.Split(script);

        Assert.That(batches, Has.Count.EqualTo(3));
    }

    [Test]
    public void Split_CustomDelimiterRemoved_FromOutput()
    {
        var script = "DELIMITER $$\nCREATE FUNCTION f1()$$\nDELIMITER ;";
        var batches = BatchSplitter.Split(script);

        Assert.That(batches[0], Does.Not.EndWith("$$"));
    }

    [Test]
    public void Split_EmptyScript_ReturnsSingleBatch()
    {
        var script = "";
        var batches = BatchSplitter.Split(script);

        Assert.That(batches, Has.Count.EqualTo(1));
    }

    [Test]
    public void Split_PendingStatementAtEnd_IsIncluded()
    {
        var script = "DELIMITER //\nSELECT 1";
        var batches = BatchSplitter.Split(script);

        Assert.That(batches, Has.Count.EqualTo(1));
        Assert.That(batches[0], Does.Contain("SELECT 1"));
    }

    // B27: line mode was switched on by the word anywhere in the script, so a procedure body in a script that only
    // mentioned DELIMITER in a comment was cut at its first semicolon.
    [Test]
    public void Split_TheWordInACommentOnly_IsNotADelimiterCommand()
    {
        var script = "-- no DELIMITER needed here\nCREATE PROCEDURE p() BEGIN\n  SELECT 1;\n  SELECT 2;\nEND";
        Assert.That(BatchSplitter.Split(script), Has.Count.EqualTo(1));
    }

    // B27: a delimiter at the end of a line inside a string or a comment ended the statement there.
    [TestCase("DELIMITER //\nCREATE PROCEDURE p() BEGIN\n  SELECT 'a //\nb';\nEND //\nDELIMITER ;")]
    [TestCase("DELIMITER //\n/* first //\n*/ CREATE PROCEDURE p() BEGIN SELECT 1; END //\nDELIMITER ;")]
    [TestCase("DELIMITER ;\nINSERT INTO t VALUES ('x;\ny');")]
    [TestCase("DELIMITER ;\nINSERT INTO t VALUES (\"x;\ny\");")]
    [TestCase("DELIMITER ;\nINSERT INTO t VALUES ('it\\'s;\ny');")]
    [TestCase("DELIMITER ;\nSELECT 1 -- the end;\n+ 1;")]
    public void Split_ADelimiterInsideAStringOrComment_DoesNotEndTheStatement(string script)
    {
        Assert.That(BatchSplitter.Split(script), Has.Count.EqualTo(1), string.Join(" | ", BatchSplitter.Split(script)));
    }

    [Test]
    public void Split_AfterAClosedString_TheDelimiterStillEndsTheStatement()
    {
        var script = "DELIMITER ;\nINSERT INTO t VALUES ('x;\ny');\nSELECT 2;";
        Assert.That(BatchSplitter.Split(script), Has.Count.EqualTo(2));
    }
}
