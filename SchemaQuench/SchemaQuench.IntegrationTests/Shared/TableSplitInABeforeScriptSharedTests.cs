// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// #613, through a whole deploy: the package stops declaring an owned table and ships a Before script that turns it into
/// a same-named view (the staged table split). The script runs before the table pass, so by the time drop-by-absence
/// looks the name is a view, and it is left alone. On PostgreSQL the drop list was decided before the Before slot ran,
/// so the deploy that ran the script failed dropping the view as a table.
/// </summary>
[NonParallelizable]
public abstract class TableSplitInABeforeScriptSharedTests : ObjectScriptDeployFixture
{
    protected abstract string TableFileName(string table);
    protected abstract string TableJson(string table);
    /// <summary>Copies nothing: drops the table and puts a view of the same name over vs_keep.</summary>
    protected abstract string SplitScript { get; }
    protected abstract string DropAll { get; }
    protected abstract string ViewCount { get; }

    [SetUp]
    public void AnOwnedTable()
    {
        Exec(DropAll);
        WritePackage("");
        WriteFile("Tables", TableFileName("vs_customer"), TableJson("vs_customer"));
        WriteFile("Tables", TableFileName("vs_keep"), TableJson("vs_keep"));
        Deploy();
    }

    [TearDown]
    public void DropIt() => Exec(DropAll);

    [Test]
    public void ATableABeforeScriptTurnsIntoAView_IsNotDroppedAsATable()
    {
        WritePackage("""{ "FolderPath": "Before", "QuenchSlot": "Before" }""", "Before");
        WriteFile("Tables", TableFileName("vs_keep"), TableJson("vs_keep"));
        WriteFile("Before", "split.sql", SplitScript);

        Deploy(dropRemovedTables: true);
        Assert.That(Count(ViewCount), Is.EqualTo(1), "the view the script made is left alone");
        Deploy(dropRemovedTables: true);
    }
}
