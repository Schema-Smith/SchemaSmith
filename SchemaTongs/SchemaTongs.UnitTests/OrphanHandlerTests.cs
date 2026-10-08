// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using log4net;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.UnitTests;

[TestFixture]
public class OrphanHandlerTests
{
    private IFile _file;
    private IDirectory _directory;
    private ILog _progressLog;

    [SetUp]
    public void SetUp()
    {
        _file = Substitute.For<IFile>();
        _directory = Substitute.For<IDirectory>();
        _progressLog = Substitute.For<ILog>();
        FactoryContainer.Register(_file);
        FactoryContainer.Register(_directory);
        LogFactory.Register("ProgressLog", _progressLog);

        _directory.Exists(Arg.Any<string>()).Returns(false);
        _directory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns([]);
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    private ExtractionFileIndex CreateIndexWithOrphans(string basePath, string[] indexed, string[] written)
    {
        _directory.Exists(basePath).Returns(true);
        _directory.GetFiles(basePath, "*.*", SearchOption.AllDirectories).Returns(
            indexed);

        var index = new ExtractionFileIndex();
        index.BuildIndex(basePath);

        foreach (var w in written)
            index.MarkWritten(w);

        return index;
    }

    [Test]
    public void ProcessOrphans_DetectMode_LogsOrphans_NoScriptsGenerated_NoFilesDeleted()
    {
        var viewsPath = Path.Combine("C:", "pkg", "template", "Views");
        var orphanFile = Path.Combine(viewsPath, "dbo.OldView.sql");

        var index = CreateIndexWithOrphans(viewsPath, [orphanFile], []);

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Views"] = index };
        var fullyExtracted = new HashSet<string> { "Views" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Views"] = ScriptObjectType.Views };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.Detect, fullyExtracted, "C:\\logs", folderObjectTypes);

        _progressLog.Received().Warn(Arg.Is<string>(s => s.Contains("dbo.OldView.sql")));
        _file.DidNotReceive().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
        _file.DidNotReceive().Delete(Arg.Any<string>());
    }

    [Test]
    public void ProcessOrphans_DetectWithCleanupScripts_GeneratesScripts_NoFilesDeleted()
    {
        var viewsPath = Path.Combine("C:", "pkg", "template", "Views");
        var orphan1 = Path.Combine(viewsPath, "dbo.OldView.sql");
        var orphan2 = Path.Combine(viewsPath, "dbo.AnotherOld.sql");

        var index = CreateIndexWithOrphans(viewsPath, [orphan1, orphan2], []);

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Views"] = index };
        var fullyExtracted = new HashSet<string> { "Views" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Views"] = ScriptObjectType.Views };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectWithCleanupScripts,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.Received().WriteAllText(
            Arg.Is<string>(s => s.Contains("_OrphanCleanup_Views.sql")),
            Arg.Is<string>(s => s.Contains("DROP VIEW IF EXISTS [dbo].[OldView];")
                             && s.Contains("DROP VIEW IF EXISTS [dbo].[AnotherOld];")));
        _file.DidNotReceive().Delete(Arg.Any<string>());

        _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("_OrphanCleanup_Views.sql") && s.Contains("2 DROP")));
    }

    [Test]
    public void ProcessOrphans_DetectDeleteAndCleanup_GeneratesScripts_AndDeletesFiles()
    {
        var viewsPath = Path.Combine("C:", "pkg", "template", "Views");
        var orphanFile = Path.Combine(viewsPath, "dbo.OldView.sql");

        var index = CreateIndexWithOrphans(viewsPath, [orphanFile], []);

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Views"] = index };
        var fullyExtracted = new HashSet<string> { "Views" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Views"] = ScriptObjectType.Views };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectDeleteAndCleanup,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.Received().WriteAllText(
            Arg.Is<string>(s => s.Contains("_OrphanCleanup_Views.sql")),
            Arg.Any<string>());
        _file.Received().Delete(orphanFile);
    }

    [Test]
    public void ProcessOrphans_FolderNotInFullyExtracted_IsSkipped()
    {
        var viewsPath = Path.Combine("C:", "pkg", "template", "Views");
        var orphanFile = Path.Combine(viewsPath, "dbo.OldView.sql");

        var index = CreateIndexWithOrphans(viewsPath, [orphanFile], []);

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Views"] = index };
        var fullyExtracted = new HashSet<string>(); // Views NOT in the set
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Views"] = ScriptObjectType.Views };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectDeleteAndCleanup,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.DidNotReceive().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
        _file.DidNotReceive().Delete(Arg.Any<string>());
        _progressLog.DidNotReceive().Warn(Arg.Any<string>());
    }

    [Test]
    public void ProcessOrphans_EmptyOrphanList_NoScriptsGenerated()
    {
        var viewsPath = Path.Combine("C:", "pkg", "template", "Views");
        var writtenFile = Path.Combine(viewsPath, "dbo.CurrentView.sql");

        var index = CreateIndexWithOrphans(viewsPath, [writtenFile], [writtenFile]);

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Views"] = index };
        var fullyExtracted = new HashSet<string> { "Views" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Views"] = ScriptObjectType.Views };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectWithCleanupScripts,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.DidNotReceive().WriteAllText(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public void ProcessOrphans_ExistingCleanupScripts_ArchivedToTimestampedSubdir()
    {
        var viewsPath = Path.Combine("C:", "pkg", "template", "Views");
        var orphanFile = Path.Combine(viewsPath, "dbo.OldView.sql");

        var index = CreateIndexWithOrphans(viewsPath, [orphanFile], []);

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Views"] = index };
        var fullyExtracted = new HashSet<string> { "Views" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Views"] = ScriptObjectType.Views };

        // Simulate existing cleanup script
        var logsDir = Path.Combine("C:", "logs");
        var existingScript = Path.Combine(logsDir, "_OrphanCleanup_Views.sql");
        _directory.Exists(logsDir).Returns(true);
        _directory.GetFiles(logsDir, "_OrphanCleanup_*.sql", SearchOption.TopDirectoryOnly)
            .Returns([existingScript]);
        _file.Exists(existingScript).Returns(true);

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectWithCleanupScripts,
            fullyExtracted, logsDir, folderObjectTypes);

        // Should create timestamped archive directory and move the existing script
        var expectedPrefix = logsDir + Path.DirectorySeparatorChar;
        _directory.Received().CreateDirectory(Arg.Is<string>(s => s.StartsWith(expectedPrefix) && s.Length > expectedPrefix.Length));
        _file.Received().Move(existingScript, Arg.Is<string>(s => s.Contains("_OrphanCleanup_Views.sql")));
    }

    [Test]
    public void ProcessOrphans_MultipleFolders_GeneratesSeparateScripts()
    {
        var viewsPath = Path.Combine("C:", "pkg", "template", "Views");
        var procsPath = Path.Combine("C:", "pkg", "template", "Procedures");
        var viewOrphan = Path.Combine(viewsPath, "dbo.OldView.sql");
        var procOrphan = Path.Combine(procsPath, "dbo.OldProc.sql");

        var viewIndex = CreateIndexWithOrphans(viewsPath, [viewOrphan], []);
        var procIndex = CreateIndexWithOrphans(procsPath, [procOrphan], []);

        var folderIndexes = new Dictionary<string, ExtractionFileIndex>
        {
            ["Views"] = viewIndex,
            ["Procedures"] = procIndex
        };
        var fullyExtracted = new HashSet<string> { "Views", "Procedures" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType>
        {
            ["Views"] = ScriptObjectType.Views,
            ["Procedures"] = ScriptObjectType.Procedures
        };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectWithCleanupScripts,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.Received().WriteAllText(
            Arg.Is<string>(s => s.Contains("_OrphanCleanup_Views.sql")),
            Arg.Is<string>(s => s.Contains("DROP VIEW")));
        _file.Received().WriteAllText(
            Arg.Is<string>(s => s.Contains("_OrphanCleanup_Procedures.sql")),
            Arg.Is<string>(s => s.Contains("DROP PROCEDURE")));
    }

    // The recycle-bin hooks are user-authored procedures in SchemaSmith's own namespace, which extraction never reads.
    // Treated as orphans, they were deleted from the package and the cleanup script dropped them from the database.
    [TestCase(Platform.SqlServer, "SchemaSmith.CustomTableDrop.sql")]
    [TestCase(Platform.PostgreSQL, "SchemaSmith.CustomTableRestore.sql")]
    [TestCase(Platform.MySQL, "SchemaSmith_CustomTableDrop.sql")]
    [TestCase(Platform.MariaDb, "SchemaSmith_CustomTableRestore.sql")]
    public void ProcessOrphans_DetectDeleteAndCleanup_SchemaSmithNamespaceFile_IsNeitherDroppedNorDeleted(Platform platform, string hookFile)
    {
        var procsPath = Path.Join("C:", "pkg", "template", "Procedures");
        var hook = Path.Join(procsPath, hookFile);
        var realOrphan = Path.Join(procsPath, platform.GetBasePlatform() == Platform.MySQL ? "OldProc.sql" : "dbo.OldProc.sql");

        var index = CreateIndexWithOrphans(procsPath, [hook, realOrphan], []);
        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Procedures"] = index };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Procedures"] = ScriptObjectType.Procedures };

        new OrphanHandler().ProcessOrphans(folderIndexes, platform, OrphanHandlingMode.DetectDeleteAndCleanup,
            ["Procedures"], Path.Join("C:", "logs"), folderObjectTypes);

        _file.Received(1).Delete(realOrphan);
        _file.DidNotReceive().Delete(hook);
        _file.Received(1).WriteAllText(Arg.Any<string>(), Arg.Is<string>(s => s.Contains("OldProc")));
        _file.DidNotReceive().WriteAllText(Arg.Any<string>(), Arg.Is<string>(s => s.Contains("CustomTable")));
        _progressLog.DidNotReceive().Warn(Arg.Is<string>(s => s.Contains("CustomTable")));
    }

    [Test]
    public void ProcessOrphans_DetectDeleteAndCleanup_GatedTableFile_NotDeleted()
    {
        var tablesPath = Path.Combine("C:", "pkg", "template", "Tables");
        var gatedFile = Path.Combine(tablesPath, "dbo.Gated.json");

        var index = CreateIndexWithOrphans(tablesPath, [gatedFile], []);

        _file.Exists(gatedFile).Returns(true);
        _file.ReadAllText(gatedFile).Returns("{\"Name\":\"Gated\",\"ShouldApplyExpression\":\"1=0\"}");

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Tables"] = index };
        var fullyExtracted = new HashSet<string> { "Tables" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Tables"] = ScriptObjectType.None };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectDeleteAndCleanup,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.DidNotReceive().Delete(gatedFile);
    }

    [Test]
    public void ProcessOrphans_GatedTableFile_LogsRetainedCount()
    {
        var tablesPath = Path.Combine("C:", "pkg", "template", "Tables");
        var gatedFile = Path.Combine(tablesPath, "dbo.Gated.json");

        var index = CreateIndexWithOrphans(tablesPath, [gatedFile], []);

        _file.Exists(gatedFile).Returns(true);
        _file.ReadAllText(gatedFile).Returns("{\"Name\":\"Gated\",\"ShouldApplyExpression\":\"1=0\"}");

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Tables"] = index };
        var fullyExtracted = new HashSet<string> { "Tables" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Tables"] = ScriptObjectType.None };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.Detect,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _progressLog.Received().Info(Arg.Is<string>(s => s.Contains("Retained 1 gated table declaration file(s)")));
    }

    [Test]
    public void ProcessOrphans_DetectDeleteAndCleanup_PlainTableFile_IsDeleted()
    {
        var tablesPath = Path.Combine("C:", "pkg", "template", "Tables");
        var plainFile = Path.Combine(tablesPath, "dbo.Plain.json");

        var index = CreateIndexWithOrphans(tablesPath, [plainFile], []);

        _file.Exists(plainFile).Returns(true);
        _file.ReadAllText(plainFile).Returns("{\"Name\":\"Plain\"}");

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Tables"] = index };
        var fullyExtracted = new HashSet<string> { "Tables" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Tables"] = ScriptObjectType.None };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectDeleteAndCleanup,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.Received().Delete(plainFile);
    }

    [Test]
    public void ProcessOrphans_DetectDeleteAndCleanup_GatedFileInNonTablesFolder_StillDeleted()
    {
        var viewsPath = Path.Combine("C:", "pkg", "template", "Views");
        var orphanFile = Path.Combine(viewsPath, "dbo.OldView.sql");

        var index = CreateIndexWithOrphans(viewsPath, [orphanFile], []);

        // Even if a non-Tables orphan happened to carry a gating expression in its bytes,
        // the suppression is scoped to the Tables folder only.
        _file.Exists(orphanFile).Returns(true);
        _file.ReadAllText(orphanFile).Returns("{\"Name\":\"OldView\",\"ShouldApplyExpression\":\"1=0\"}");

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Views"] = index };
        var fullyExtracted = new HashSet<string> { "Views" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Views"] = ScriptObjectType.Views };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectDeleteAndCleanup,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.Received().Delete(orphanFile);
    }

    [Test]
    public void ProcessOrphans_JsonFolder_Tables_GeneratesDropTableScript()
    {
        var tablesPath = Path.Combine("C:", "pkg", "template", "Tables");
        var orphanFile = Path.Combine(tablesPath, "dbo.OldTable.json");

        var index = CreateIndexWithOrphans(tablesPath, [orphanFile], []);

        var folderIndexes = new Dictionary<string, ExtractionFileIndex> { ["Tables"] = index };
        var fullyExtracted = new HashSet<string> { "Tables" };
        var folderObjectTypes = new Dictionary<string, ScriptObjectType> { ["Tables"] = ScriptObjectType.None };

        var handler = new OrphanHandler();
        handler.ProcessOrphans(folderIndexes, Platform.SqlServer, OrphanHandlingMode.DetectWithCleanupScripts,
            fullyExtracted, "C:\\logs", folderObjectTypes);

        _file.Received().WriteAllText(
            Arg.Is<string>(s => s.Contains("_OrphanCleanup_Tables.sql")),
            Arg.Is<string>(s => s.Contains("DROP TABLE IF EXISTS [dbo].[OldTable];")));
    }
}
