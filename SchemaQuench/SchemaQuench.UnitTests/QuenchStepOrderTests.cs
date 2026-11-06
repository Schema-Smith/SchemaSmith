// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

/// <summary>
/// The order in which a deploy calls SchemaSmith's procedures, for a real run and for a preview. Recorded against a
/// connection that answers the version queries and runs nothing, so what is pinned is the sequence SchemaQuench sends.
/// <para>These pin the order as it stands. The order is written twice in <c>DatabaseQuench.Execute</c>, once per run
/// mode, and the two copies have drifted before; a change to either list here should be a deliberate one.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class QuenchStepOrderTests
{
    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    internal sealed class Recording
    {
        public readonly List<string> Statements = [];
        public readonly List<string> CheckpointedSteps = [];
    }

    // A call to one of SchemaSmith's procedures, however qualified: EXEC [db].SchemaSmith.TableQuench, CALL "SchemaSmith".x,
    // CALL SchemaSmith_x. The functions an inline script uses are not steps.
    private static readonly Regex ProcName = new(@"\b(?:EXEC(?:UTE)?|CALL)\s+(?:\[[^\]]+\]\.|""[^""]+""\.|`[^`]+`\.)?(?:\[SchemaSmith\]|""SchemaSmith""|SchemaSmith)[._](?:\[|"")?(?<name>[A-Za-z][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The SchemaSmith procedures each statement names, in the order sent, consecutive repeats collapsed.</summary>
    // The fixture's own scripts, baseline and stamp carry a marker so they show where they ran among the procedures.
    private static readonly Regex Marker = new(@"/\*(?<name>(?:script|baseline|stamp)[^*]*)\*/", RegexOptions.Compiled);

    internal static List<string> Procedures(IEnumerable<string> statements)
    {
        var names = new List<string>();
        foreach (var statement in statements)
            foreach (Match m in Marker.Matches(statement).Concat(ProcName.Matches(statement)).OrderBy(m => m.Index))
            {
                var name = m.Groups["name"].Value;
                if (name.Length == 0 || (names.Count > 0 && names[^1] == name)) continue;
                names.Add(name);
            }
        return names;
    }

    internal static Recording Run(Platform platform, Template template, bool whatIf, bool updateTables = true, bool deliverData = false)
    {
        var recording = new Recording();
        FactoryContainer.Register<IFile>(Substitute.For<IFile>());
        FactoryContainer.Register<IDirectory>(Substitute.For<IDirectory>());
        var config = Substitute.For<IConfigurationRoot>();
        config["Target:User"].Returns("u");
        config["Target:Password"].Returns("p");
        FactoryContainer.Register<IConfigurationRoot>(config);

        var command = Substitute.For<IDbCommand>();
        var connection = Substitute.For<IDbConnection>();
        connection.Database.Returns("db");
        connection.CreateCommand().Returns(command);
        command.Connection.Returns(connection);
        command.Parameters.Returns(Substitute.For<IDataParameterCollection>());
        command.CreateParameter().Returns(_ => Substitute.For<IDbDataParameter>());
        command.ExecuteNonQuery().Returns(_ => { recording.Statements.Add(command.CommandText ?? ""); return 0; });
        command.ExecuteScalar().Returns(_ =>
        {
            var text = command.CommandText ?? "";
            recording.Statements.Add(text);
            if (text == TargetVersionDetector.GetVersionQuery(platform))
                return platform switch
                {
                    Platform.SqlServer => "16.0.4135.4",
                    Platform.PostgreSQL => "160004",
                    Platform.MariaDb => "11.4.5-MariaDB",
                    _ => "8.4.3",
                };
            if (text.Contains("compatibility_level")) return 160;
            if (text.Contains("HAS_PERMS_BY_NAME")) return 1;
            if (text.Contains("/*baseline*/")) return true;
            return null;
        });
        command.ExecuteReader().Returns(_ => { recording.Statements.Add(command.CommandText ?? ""); return new DataTable().CreateDataReader(); });
        command.ExecuteReader(Arg.Any<CommandBehavior>()).Returns(_ => { recording.Statements.Add(command.CommandText ?? ""); return new DataTable().CreateDataReader(); });

        var factory = Substitute.For<IDbConnectionFactory>();
        factory.GetDbConnection(Arg.Any<string>()).Returns(connection);
        FactoryContainer.Register<IDbConnectionFactory>(factory);

        var checkpointing = Substitute.For<ICheckpointing>();
        checkpointing.GetDatabaseCheckpointSummary(Arg.Any<TrackingScope>()).Returns(DatabaseCheckpointSummary.Empty);
        checkpointing.When(c => c.Track(Arg.Any<TrackingScope>(), Arg.Any<string>(), Arg.Any<Action>()))
            .Do(ci => { recording.CheckpointedSteps.Add(ci.ArgAt<string>(1)); ci.ArgAt<Action>(2)(); });

        var product = new Product { Name = "P", Platform = platform };
        var flag = platform == Platform.PostgreSQL ? (whatIf ? "true" : "false") : (whatIf ? "1" : "0");
        var off = platform == Platform.PostgreSQL ? "false" : "0";
        var quench = new DatabaseQuench("srv", product, template, "db", suppressKindling: true, whatIfOnly: flag, runScriptsTwice: false,
            dropRemovedTables: off, dropRemovedColumns: off, dropRemovedForeignKeys: off, dropRemovedCheckConstraints: off,
            dropRemovedExcludeConstraints: off, dropRemovedStatistics: off, dropRemovedIndexes: off, dropUnknownIndexes: off,
            updateTables: updateTables, deliverData: deliverData, checkpointing: checkpointing);
        quench.Execute();
        Assert.That(quench.QuenchSuccessful, Is.True, $"the recorded run completed: {quench.LastFailure}");
        return recording;
    }

    internal static Template TemplateWithATable(Platform platform = Platform.SqlServer)
    {
        var template = new Template { Name = "T" };
        template.Tables.Add(platform switch
        {
            Platform.SqlServer => new SqlServerTable { Name = "Orders" },
            Platform.PostgreSQL => new PostgreSqlTable { Name = "Orders" },
            Platform.MariaDb => new MariaDbTable { Name = "Orders" },
            _ => new MySqlTable { Name = "Orders" },
        });
        return template;
    }

    // The loader serialises each declared collection for its procedure; a stand-in body is enough to call it.
    private const string Declared = "[{\"Name\":\"x\"}]";

    private static SqlScript Script(string marker)
    {
        var script = new SqlScript { Name = marker + ".sql", FilePath = $"Scripts/{marker}.sql" };
        script.Batches.Add($"/*script:{marker}*/ SELECT 1");
        return script;
    }

    /// <summary>A table, one script in every slot, a baseline and a version stamp, and every optional object the
    /// engine has, so each step's predicate is true.</summary>
    internal static Template RichTemplate(Platform platform)
    {
        var template = TemplateWithATable(platform);
        template.BaselineValidationScript = "/*baseline*/ SELECT 1";
        template.VersionStampScript = "/*stamp*/ SELECT 1";
        foreach (var slot in new[] { TemplateQuenchSlot.Setup, TemplateQuenchSlot.Before, TemplateQuenchSlot.Objects, TemplateQuenchSlot.BetweenTablesAndKeys,
                                     TemplateQuenchSlot.AfterTablesScripts, TemplateQuenchSlot.AfterTablesObjects, TemplateQuenchSlot.TableData, TemplateQuenchSlot.After })
        {
            var folder = new TemplateFolder { QuenchSlot = slot, FolderPath = slot.ToString() };
            folder.Scripts.Add(Script(slot.ToString()));
            template.ScriptFolders.Add(folder);
        }
        switch (platform)
        {
            case Platform.SqlServer:
                template.IndexedViews.Add(new SqlServerIndexedView { Schema = "dbo", Name = "v", Definition = "SELECT 1 AS x",
                    Indexes = [new SqlServerIndex { Name = "ix_v", Clustered = true, Unique = true, IndexColumns = "x" }] });
                template.IndexedViewSchema = Declared;
                break;
            case Platform.PostgreSQL:
                template.DomainTypes.Add(new PostgreSqlDomainType { Schema = "public", Name = "d", DataType = "int" });
                template.EnumTypes.Add(new PostgreSqlEnumType { Schema = "public", Name = "e", Values = ["a"] });
                template.Sequences.Add(new PostgreSqlSequence { Schema = "public", Name = "s" });
                template.MaterializedViews.Add(new PostgreSqlMaterializedView { Schema = "public", Name = "mv", Definition = "SELECT 1" });
                template.DomainTypeSchema = Declared;
                template.EnumTypeSchema = Declared;
                template.SequenceSchema = Declared;
                template.MaterializedViewSchema = Declared;
                break;
            case Platform.MariaDb:
                template.MariaDbSequences.Add(new MariaDbSequence { Name = "s" });
                template.Events.Add(new MySqlEvent { Name = "ev", Definition = "DO SELECT 1" });
                template.SequenceSchema = Declared;
                template.EventSchema = Declared;
                break;
            case Platform.MySQL:
                template.Events.Add(new MySqlEvent { Name = "ev", Definition = "DO SELECT 1" });
                template.EventSchema = Declared;
                break;
        }
        return template;
    }

    public static IEnumerable<TestCaseData> Scenarios()
    {
        foreach (var platform in new[] { Platform.SqlServer, Platform.PostgreSQL, Platform.MySQL, Platform.MariaDb })
            foreach (var whatIf in new[] { false, true })
            {
                yield return new TestCaseData(platform, whatIf, "rich").SetName($"{{m}}({platform},{(whatIf ? "WhatIf" : "Quench")},rich)");
                yield return new TestCaseData(platform, whatIf, "indexOnly").SetName($"{{m}}({platform},{(whatIf ? "WhatIf" : "Quench")},indexOnly)");
                yield return new TestCaseData(platform, whatIf, "noTables").SetName($"{{m}}({platform},{(whatIf ? "WhatIf" : "Quench")},noTables)");
                yield return new TestCaseData(platform, whatIf, "deliver").SetName($"{{m}}({platform},{(whatIf ? "WhatIf" : "Quench")},deliver)");
            }
    }

    internal static Recording RunScenario(Platform platform, bool whatIf, string scenario)
    {
        var template = RichTemplate(platform);
        if (scenario == "indexOnly") template.IndexOnlyTableQuenches = true;
        return Run(platform, template, whatIf, updateTables: scenario != "noTables", deliverData: scenario == "deliver");
    }

    // Procedures called, in order (with the fixture's script, baseline and stamp markers), then the steps checkpointed.
    private static readonly Dictionary<string, (string Procedures, string Checkpointed)> Golden = new()
    {
        ["SqlServer|Quench|rich"] = ("baseline,script:Setup,script:Objects,StageGraphKeyParts,LiftPeriodColumns,LiftLedgerColumns,ValidateTableOwnership,DegradeUnsupportedFeatures,CdcPreflight,ExpressionMapAdopt,MissingTableAndColumnQuench,script:Before,DegradeDatabaseToggles,ModifiedTableQuench,script:AfterTablesObjects,script:BetweenTablesAndKeys,MissingIndexesAndConstraintsQuench,IndexStateQuench,FileStreamColumnQuench,CdcQuench,ChangeTrackingQuench,ExpressionMapRecord,script:AfterTablesScripts,ForeignKeyQuench,EdgeConstraintQuench,ConstraintStateQuench,ExtendedPropertyQuench,IndexedViewQuench,script:After,stamp",
            "ValidateBaseline,ModifiedTables,IndexesAndConstraints,TableFeatures,ForeignKeys,IndexedViewQuench,VersionStamp"),
        ["SqlServer|WhatIf|rich"] = ("baseline,StageGraphKeyParts,LiftPeriodColumns,LiftLedgerColumns,ValidateTableOwnership,DegradeUnsupportedFeatures,CdcPreflight,ExpressionMapAdopt,MissingTableAndColumnQuench,DegradeDatabaseToggles,ModifiedTableQuench,MissingIndexesAndConstraintsQuench,IndexStateQuench,FileStreamColumnQuench,CdcQuench,ChangeTrackingQuench,ExpressionMapRecord,ForeignKeyQuench,EdgeConstraintQuench,ConstraintStateQuench,ExtendedPropertyQuench,IndexedViewQuench",
            ""),
        ["SqlServer|Quench|indexOnly"] = ("baseline,script:Setup,script:Objects,script:Before,script:AfterTablesObjects,script:BetweenTablesAndKeys,IndexOnlyQuench,ExpressionMapRecord,script:AfterTablesScripts,IndexedViewQuench,script:After,stamp",
            "ValidateBaseline,IndexesAndConstraints,IndexedViewQuench,VersionStamp"),
        ["SqlServer|WhatIf|indexOnly"] = ("baseline,IndexOnlyQuench,ExpressionMapRecord,IndexedViewQuench",
            ""),
        ["SqlServer|Quench|noTables"] = ("baseline,script:Setup,script:Objects,script:Before,script:AfterTablesObjects,script:BetweenTablesAndKeys,script:AfterTablesScripts,IndexedViewQuench,script:After,stamp",
            "ValidateBaseline,IndexedViewQuench,VersionStamp"),
        ["SqlServer|WhatIf|noTables"] = ("baseline,IndexedViewQuench",
            ""),
        ["SqlServer|Quench|deliver"] = ("baseline,script:Setup,script:Objects,StageGraphKeyParts,LiftPeriodColumns,LiftLedgerColumns,ValidateTableOwnership,DegradeUnsupportedFeatures,CdcPreflight,ExpressionMapAdopt,MissingTableAndColumnQuench,script:Before,DegradeDatabaseToggles,ModifiedTableQuench,script:AfterTablesObjects,script:BetweenTablesAndKeys,MissingIndexesAndConstraintsQuench,IndexStateQuench,FileStreamColumnQuench,CdcQuench,ChangeTrackingQuench,ExpressionMapRecord,script:AfterTablesScripts,script:TableData,ForeignKeyQuench,EdgeConstraintQuench,ConstraintStateQuench,ExtendedPropertyQuench,IndexedViewQuench,script:After,stamp",
            "ValidateBaseline,ModifiedTables,IndexesAndConstraints,TableFeatures,TableDataDelivery,ForeignKeys,IndexedViewQuench,VersionStamp"),
        ["SqlServer|WhatIf|deliver"] = ("baseline,StageGraphKeyParts,LiftPeriodColumns,LiftLedgerColumns,ValidateTableOwnership,DegradeUnsupportedFeatures,CdcPreflight,ExpressionMapAdopt,MissingTableAndColumnQuench,DegradeDatabaseToggles,ModifiedTableQuench,MissingIndexesAndConstraintsQuench,IndexStateQuench,FileStreamColumnQuench,CdcQuench,ChangeTrackingQuench,ExpressionMapRecord,ForeignKeyQuench,EdgeConstraintQuench,ConstraintStateQuench,ExtendedPropertyQuench,IndexedViewQuench",
            ""),
        ["PostgreSQL|Quench|rich"] = ("baseline,script:Setup,script:Objects,DomainTypeQuench,EnumTypeQuench,SequenceQuench,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,script:Before,ModifiedTableQuench,script:AfterTablesObjects,script:BetweenTablesAndKeys,MissingIndexesAndConstraintsQuench,DescriptionQuench,ReplicaIdentityQuench,FixupTableOwnership,FixupIndexOwnership,ExpressionMapRecord,script:AfterTablesScripts,ForeignKeyQuench,ConstraintStateQuench,MaterializedViewQuench,script:After,stamp",
            "ValidateBaseline,DomainTypeQuench,EnumTypeQuench,SequenceQuench,ModifiedTables,IndexesAndConstraints,ForeignKeys,MaterializedViewQuench,VersionStamp"),
        ["PostgreSQL|WhatIf|rich"] = ("baseline,DomainTypeQuench,EnumTypeQuench,SequenceQuench,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,ModifiedTableQuench,MissingIndexesAndConstraintsQuench,DescriptionQuench,ReplicaIdentityQuench,FixupTableOwnership,FixupIndexOwnership,ExpressionMapRecord,ForeignKeyQuench,ConstraintStateQuench,MaterializedViewQuench",
            ""),
        ["PostgreSQL|Quench|indexOnly"] = ("baseline,script:Setup,script:Objects,DomainTypeQuench,EnumTypeQuench,SequenceQuench,script:Before,script:AfterTablesObjects,script:BetweenTablesAndKeys,IndexOnlyQuench,ReplicaIdentityQuench,FixupIndexOwnership,ExpressionMapRecord,script:AfterTablesScripts,MaterializedViewQuench,script:After,stamp",
            "ValidateBaseline,DomainTypeQuench,EnumTypeQuench,SequenceQuench,IndexesAndConstraints,MaterializedViewQuench,VersionStamp"),
        ["PostgreSQL|WhatIf|indexOnly"] = ("baseline,DomainTypeQuench,EnumTypeQuench,SequenceQuench,IndexOnlyQuench,ReplicaIdentityQuench,FixupIndexOwnership,ExpressionMapRecord,MaterializedViewQuench",
            ""),
        ["PostgreSQL|Quench|noTables"] = ("baseline,script:Setup,script:Objects,DomainTypeQuench,EnumTypeQuench,SequenceQuench,script:Before,script:AfterTablesObjects,script:BetweenTablesAndKeys,script:AfterTablesScripts,MaterializedViewQuench,script:After,stamp",
            "ValidateBaseline,DomainTypeQuench,EnumTypeQuench,SequenceQuench,MaterializedViewQuench,VersionStamp"),
        ["PostgreSQL|WhatIf|noTables"] = ("baseline,DomainTypeQuench,EnumTypeQuench,SequenceQuench,MaterializedViewQuench",
            ""),
        ["PostgreSQL|Quench|deliver"] = ("baseline,script:Setup,script:Objects,DomainTypeQuench,EnumTypeQuench,SequenceQuench,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,script:Before,ModifiedTableQuench,script:AfterTablesObjects,script:BetweenTablesAndKeys,MissingIndexesAndConstraintsQuench,DescriptionQuench,ReplicaIdentityQuench,FixupTableOwnership,FixupIndexOwnership,ExpressionMapRecord,script:AfterTablesScripts,script:TableData,ForeignKeyQuench,ConstraintStateQuench,MaterializedViewQuench,script:After,stamp",
            "ValidateBaseline,DomainTypeQuench,EnumTypeQuench,SequenceQuench,ModifiedTables,IndexesAndConstraints,TableDataDelivery,ForeignKeys,MaterializedViewQuench,VersionStamp"),
        ["PostgreSQL|WhatIf|deliver"] = ("baseline,DomainTypeQuench,EnumTypeQuench,SequenceQuench,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,ModifiedTableQuench,MissingIndexesAndConstraintsQuench,DescriptionQuench,ReplicaIdentityQuench,FixupTableOwnership,FixupIndexOwnership,ExpressionMapRecord,ForeignKeyQuench,ConstraintStateQuench,MaterializedViewQuench",
            ""),
        ["MySQL|Quench|rich"] = ("baseline,script:Setup,script:Objects,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,script:Before,ModifiedTableQuench,script:AfterTablesObjects,script:BetweenTablesAndKeys,MissingIndexesAndConstraintsQuench,ExpressionMapRecord,script:AfterTablesScripts,ForeignKeyQuench,EventQuench,script:After,stamp",
            "ValidateBaseline,ModifiedTables,IndexesAndConstraints,ForeignKeys,EventQuench,VersionStamp"),
        ["MySQL|WhatIf|rich"] = ("baseline,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,ModifiedTableQuench,MissingIndexesAndConstraintsQuench,ExpressionMapRecord,ForeignKeyQuench,EventQuench",
            ""),
        ["MySQL|Quench|indexOnly"] = ("baseline,script:Setup,script:Objects,script:Before,script:AfterTablesObjects,script:BetweenTablesAndKeys,IndexOnlyQuench,ExpressionMapRecord,script:AfterTablesScripts,EventQuench,script:After,stamp",
            "ValidateBaseline,IndexesAndConstraints,EventQuench,VersionStamp"),
        ["MySQL|WhatIf|indexOnly"] = ("baseline,IndexOnlyQuench,ExpressionMapRecord,EventQuench",
            ""),
        ["MySQL|Quench|noTables"] = ("baseline,script:Setup,script:Objects,script:Before,script:AfterTablesObjects,script:BetweenTablesAndKeys,script:AfterTablesScripts,EventQuench,script:After,stamp",
            "ValidateBaseline,EventQuench,VersionStamp"),
        ["MySQL|WhatIf|noTables"] = ("baseline,EventQuench",
            ""),
        ["MySQL|Quench|deliver"] = ("baseline,script:Setup,script:Objects,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,script:Before,ModifiedTableQuench,script:AfterTablesObjects,script:BetweenTablesAndKeys,MissingIndexesAndConstraintsQuench,ExpressionMapRecord,script:AfterTablesScripts,script:TableData,ForeignKeyQuench,EventQuench,script:After,stamp",
            "ValidateBaseline,ModifiedTables,IndexesAndConstraints,TableDataDelivery,ForeignKeys,EventQuench,VersionStamp"),
        ["MySQL|WhatIf|deliver"] = ("baseline,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,ModifiedTableQuench,MissingIndexesAndConstraintsQuench,ExpressionMapRecord,ForeignKeyQuench,EventQuench",
            ""),
        ["MariaDb|Quench|rich"] = ("baseline,script:Setup,script:Objects,SequenceQuench,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,script:Before,ModifiedTableQuench,script:AfterTablesObjects,script:BetweenTablesAndKeys,MissingIndexesAndConstraintsQuench,ExpressionMapRecord,script:AfterTablesScripts,ForeignKeyQuench,EventQuench,script:After,stamp",
            "ValidateBaseline,SequenceQuench,ModifiedTables,IndexesAndConstraints,ForeignKeys,EventQuench,VersionStamp"),
        ["MariaDb|WhatIf|rich"] = ("baseline,SequenceQuench,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,ModifiedTableQuench,MissingIndexesAndConstraintsQuench,ExpressionMapRecord,ForeignKeyQuench,EventQuench",
            ""),
        ["MariaDb|Quench|indexOnly"] = ("baseline,script:Setup,script:Objects,SequenceQuench,script:Before,script:AfterTablesObjects,script:BetweenTablesAndKeys,IndexOnlyQuench,ExpressionMapRecord,script:AfterTablesScripts,EventQuench,script:After,stamp",
            "ValidateBaseline,SequenceQuench,IndexesAndConstraints,EventQuench,VersionStamp"),
        ["MariaDb|WhatIf|indexOnly"] = ("baseline,SequenceQuench,IndexOnlyQuench,ExpressionMapRecord,EventQuench",
            ""),
        ["MariaDb|Quench|noTables"] = ("baseline,script:Setup,script:Objects,SequenceQuench,script:Before,script:AfterTablesObjects,script:BetweenTablesAndKeys,script:AfterTablesScripts,EventQuench,script:After,stamp",
            "ValidateBaseline,SequenceQuench,EventQuench,VersionStamp"),
        ["MariaDb|WhatIf|noTables"] = ("baseline,SequenceQuench,EventQuench",
            ""),
        ["MariaDb|Quench|deliver"] = ("baseline,script:Setup,script:Objects,SequenceQuench,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,script:Before,ModifiedTableQuench,script:AfterTablesObjects,script:BetweenTablesAndKeys,MissingIndexesAndConstraintsQuench,ExpressionMapRecord,script:AfterTablesScripts,script:TableData,ForeignKeyQuench,EventQuench,script:After,stamp",
            "ValidateBaseline,SequenceQuench,ModifiedTables,IndexesAndConstraints,TableDataDelivery,ForeignKeys,EventQuench,VersionStamp"),
        ["MariaDb|WhatIf|deliver"] = ("baseline,SequenceQuench,ParseTableJson,ValidateTableOwnership,ExpressionMapAdopt,MissingTableAndColumnQuench,ModifiedTableQuench,MissingIndexesAndConstraintsQuench,ExpressionMapRecord,ForeignKeyQuench,EventQuench",
            ""),
    };

    [TestCaseSource(nameof(Scenarios))]
    public void TheDeploySendsItsStepsInTheRecordedOrder(Platform platform, bool whatIf, string scenario)
    {
        var key = $"{platform}|{(whatIf ? "WhatIf" : "Quench")}|{scenario}";
        var recording = RunScenario(platform, whatIf, scenario);
        Assert.Multiple(() =>
        {
            Assert.That(string.Join(",", Procedures(recording.Statements)), Is.EqualTo(Golden[key].Procedures), $"{key}: procedure order");
            Assert.That(string.Join(",", recording.CheckpointedSteps), Is.EqualTo(Golden[key].Checkpointed), $"{key}: checkpointed steps");
        });
    }
}
