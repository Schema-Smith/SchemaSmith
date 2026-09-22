// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer
{
    /// <summary>
    /// The keystone guard for the client-ingest path.
    /// <para>
    /// Two producers fill one working set: the engine's JSON shred, and the client bulk-loading rows it
    /// built itself. The entire design rests on them agreeing, and nothing about a disagreement is loud —
    /// a column the client writes as NULL, or a row it orders differently, produces a deploy that runs
    /// clean and converges the wrong thing. So the paths are run over the same model and the resulting
    /// tables compared row for row. Without this test the two-producer design is a silent-divergence
    /// machine; with it, a divergence is a failing build.
    /// </para>
    /// </summary>
    [TestFixture]
    [Category("SqlServer")]
    public class BulkIngestEquivalenceTests : BaseTableQuenchTests
    {
        // Deliberately exercises what a client row-writer is most likely to get wrong: an absent property
        // versus an explicit null, a nested object read through dotted paths, booleans spelled as JSON
        // literals, a table declaring no children at all, and two tables so row identity has to be right.
        // DATETIME2 / TIME / DATETIMEOFFSET are here deliberately: those three canonicalize to an
        // explicit (7) for COMPARISON while the emitted DDL must stay un-suffixed, so they are the case
        // where getting the normalize ORDER wrong changes the generated SQL. A rewrite did exactly that
        // and neither equivalence guard caught it, because the fixture had no such column.
        // The two computed columns cover the other half: an OMITTED Nullable must leave the engine's
        // derived nullability alone, while an explicit false on a PERSISTED column asks for NOT NULL.
        private const string RichModelJson = @"[{
  ""Schema"":""dbo"",""Name"":""BulkEquivTable"",""CompressionType"":""PAGE"",""IsTemporal"":true,
  ""HistoryTableSchema"":""history"",""HistoryTableName"":""BulkEquivTable_Archive"",""HistoryRetentionPeriod"":""5 YEARS"",
  ""FileGroup"":""FG_Test"",""UpdateFillFactor"":false,""OldName"":null,""EnableCDC"":false,""PreventDrop"":false,
  ""GraphType"":""Node"",""Ledger"":""Off"",""MemoryOptimized"":false,""Durability"":""SCHEMA_AND_DATA"",
  ""EnableChangeTracking"":true,""TrackColumnsUpdated"":false,
  ""DropColumnsRemovedFromProduct"":true,""DropForeignKeysRemovedFromProduct"":false,
  ""DropCheckConstraintsRemovedFromProduct"":false,""DropExcludeConstraintsRemovedFromProduct"":false,
  ""DropStatisticsRemovedFromProduct"":false,""DropIndexesRemovedFromProduct"":true,
  ""RebuildPolicy"":{""Mode"":""ALWAYS"",""Threshold"":25,""OnOrderMismatch"":true},
  ""Columns"":[
    {""Name"":""Id"",""DataType"":""INT"",""Nullable"":false},
    {""Name"":""Amount"",""DataType"":""DECIMAL(10,2)"",""Nullable"":true,""Default"":""0""},
    {""Name"":""Note"",""DataType"":""NVARCHAR(200)"",""Nullable"":true,""Collation"":""SQL_Latin1_General_CP1_CI_AS""},
    {""Name"":""Stamp"",""DataType"":""DATETIME2"",""Nullable"":true},
    {""Name"":""Clock"",""DataType"":""TIME"",""Nullable"":true},
    {""Name"":""Zoned"",""DataType"":""DATETIMEOFFSET"",""Nullable"":true},
    {""Name"":""Derived"",""DataType"":""INT"",""ComputedExpression"":""Id + 1"",""Persisted"":true},
    {""Name"":""DerivedNotNull"",""DataType"":""INT"",""ComputedExpression"":""Id + 2"",""Persisted"":true,""Nullable"":false}
  ],
  ""Indexes"":[
    {""Name"":""PK_BulkEquivTable"",""PrimaryKey"":true,""Unique"":true,""Clustered"":true,""IndexColumns"":""Id""},
    {""Name"":""IX_Amount"",""Unique"":false,""IndexColumns"":""Amount DESC"",""IncludeColumns"":""Note"",""FileGroup"":""FG_Test""}
  ],
  ""XmlIndexes"":[
    {""Name"":""XI_Data"",""IsPrimary"":true,""Column"":""DataXml"",""PrimaryIndex"":null,""SecondaryIndexType"":null}
  ],
  ""ForeignKeys"":[
    {""Name"":""FK_BulkEquivTable_Other"",""Columns"":""Id"",""RelatedTableSchema"":""dbo"",""RelatedTable"":""OtherTable"",""RelatedColumns"":""OtherId"",""DeleteAction"":""CASCADE""}
  ],
  ""CheckConstraints"":[
    {""Name"":""CK_Amount"",""Expression"":""Amount >= 0""}
  ],
  ""Statistics"":[
    {""Name"":""ST_Note"",""Columns"":""Note"",""SampleSize"":50}
  ],
  ""FullTextIndex"":[
    {""Columns"":""Note LANGUAGE 1033"",""FullTextCatalog"":""ftCat"",""KeyIndex"":""PK_BulkEquivTable"",""ChangeTracking"":""AUTO"",""StopList"":""SYSTEM""}
  ]
},{
  ""Schema"":""dbo"",""Name"":""BulkEquivBare"",
  ""Columns"":[{""Name"":""Id"",""DataType"":""INT"",""Nullable"":false}]
}]";

        // Every table a downstream proc consumes, plus the staging table the client actually loads --
        // comparing only the consumed ones would let a fault in the loaded table hide behind the shreds
        // that read it back out.
        private static readonly string[] ComparedTables =
        {
            "#TableDefinitions", "#Tables", "#Columns", "#Indexes", "#XmlIndexes",
            "#ForeignKeys", "#CheckConstraints", "#Statistics", "#FullTextIndexes"
        };

        [Test]
        public void ClientBuiltWorkingSet_IsIdenticalTo_TheEngineShred()
        {
            using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer)
                .GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            var shredded = CaptureViaShred(conn);
            var bulkLoaded = CaptureViaClientBuild(conn);

            Assert.Multiple(() =>
            {
                for (var i = 0; i < ComparedTables.Length; i++)
                {
                    Assert.That(shredded[i], Is.Not.Empty,
                        $"{ComparedTables[i]}: the shred produced no rows — the fixture does not exercise this table, " +
                        "so equality here would prove nothing.");
                    Assert.That(bulkLoaded[i], Is.EqualTo(shredded[i]),
                        $"{ComparedTables[i]}: the client-built working set differs from the engine shred.");
                }
            });
        }

        [Test]
        public void ClientRowBuilder_RefusesAColumnItWasNotTaughtToFill()
        {
            // The failure this protects against is silence: a column added to the working set that the
            // client does not know about would bulk-load as NULL and deploy the wrong thing. Prove the
            // refusal is real rather than assuming the production shape happens to be complete.
            using var shape = new DataTable("#Probe");
            shape.Columns.Add("Mapped", typeof(string));
            shape.Columns.Add("AddedToSqlButNotToTheClient", typeof(string));

            var map = new[] { new WorkingSetShredMap.ShredColumn("Mapped", "NVARCHAR(50)", "$.Mapped", false) };

            var ex = Assert.Throws<System.Exception>(() =>
                WorkingSetRowBuilder.Build(JArray.Parse(@"[{""Mapped"":""x""}]"), map, shape));

            Assert.That(ex.Message, Does.Contain("AddedToSqlButNotToTheClient"));
        }

        // ---- the two paths ------------------------------------------------------------------------

        private static List<List<string>> CaptureViaShred(SqlConnection conn)
        {
            var (createTables, fillTables) = ForgeKindler.GetParseTableJsonPhases(Platform.SqlServer);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = createTables;
            cmd.ExecuteNonQuery();

            cmd.CommandText = "DECLARE @v_SQL NVARCHAR(MAX) = ''\nSET NOCOUNT ON\n" + fillTables;
            AddPayload(cmd, RichModelJson);
            cmd.ExecuteNonQuery();

            return Capture(conn);
        }

        private static List<List<string>> CaptureViaClientBuild(SqlConnection conn)
        {
            var (createTables, fillTables) = ForgeKindler.GetParseTableJsonPhases(Platform.SqlServer);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = createTables;
            cmd.ExecuteNonQuery();

            using var shape = new DataTable("#TableDefinitions");
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT TOP 0 * FROM #TableDefinitions";
                using var reader = probe.ExecuteReader(CommandBehavior.SchemaOnly);
                shape.Load(reader);
            }

            WorkingSetRowBuilder.Build(JArray.Parse(RichModelJson),
                WorkingSetShredMap.For(Platform.SqlServer, "#TableDefinitions"), shape);
            Load(conn, shape);

            // #Columns is built from the nested array, so it also proves the parent-supplied Schema and
            // TableName and the continuous _RowId numbering the gating DELETEs key on.
            using var columns = new DataTable("#Columns");
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT TOP 0 * FROM #Columns";
                using var reader = probe.ExecuteReader(CommandBehavior.SchemaOnly);
                columns.Load(reader);
            }
            WorkingSetRowBuilder.BuildChild(JArray.Parse(RichModelJson), "Columns",
                WorkingSetShredMap.For(Platform.SqlServer, "#Columns"), columns);
            Load(conn, columns);

            // The shred for the table just loaded is REMOVED, not skipped, and the rest of the fill --
            // normalize, gating, and every child shred reading this table's nested JSON -- runs as usual.
            cmd.CommandText = "DECLARE @v_SQL NVARCHAR(MAX) = ''\nSET NOCOUNT ON\n"
                              + ForgeKindler.RemoveShredRegion(
                                    ForgeKindler.RemoveShredRegion(fillTables, "#TableDefinitions"), "#Columns");
            AddPayload(cmd, RichModelJson);
            cmd.ExecuteNonQuery();

            return Capture(conn);
        }


        private static void Load(SqlConnection conn, DataTable rows)
        {
            using var bulk = new SqlBulkCopy(conn) { DestinationTableName = rows.TableName };
            foreach (DataColumn column in rows.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            bulk.WriteToServer(rows);
        }

        private static void AddPayload(SqlCommand cmd, string json)
        {
            cmd.Parameters.Clear();
            var payload = cmd.Parameters.Add("@TableDefinitions", SqlDbType.VarChar, -1);
            payload.Value = json;
            cmd.Parameters.Add("@UpdateFillFactor", SqlDbType.Bit).Value = false;
        }

        // Columns the shred fills with AS JSON. OPENJSON hands back the payload's ORIGINAL text for these,
        // whitespace and all, while a client re-serializes from a parsed model -- so the two agree on the
        // JSON and disagree on its formatting. Their contract is the JSON, because the only thing that
        // ever reads them is the child shred below, and that is insensitive to layout. They are compared
        // as JSON; the child tables they produce are compared byte for byte, so a real difference in
        // content still fails, just one table further down.
        private static readonly HashSet<string> JsonValuedColumns =
            WorkingSetShredMap.For(Platform.SqlServer, "#TableDefinitions")
                .Where(c => c.AsJson).Select(c => c.Column)
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        // Per table, a sorted list of row signatures. _RowId is included deliberately: gating generates
        // DELETEs keyed on it, so the two paths agreeing on row identity is part of what must be proven,
        // not an incidental detail to normalize away.
        private static List<List<string>> Capture(SqlConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.Concat(ComparedTables.Select(t => $"SELECT * FROM {t};\n"));

            var perTable = new List<List<string>>();
            using var reader = cmd.ExecuteReader();
            do
            {
                // The JSON-valued columns belong to #TableDefinitions alone. Applied everywhere, the
                // canonicalization hit #ForeignKeys.Columns -- same column name, an ordinary string like
                // "Id" -- and blew up trying to parse it as JSON.
                var isStagingTable = ComparedTables[perTable.Count] == "#TableDefinitions";

                var rows = new List<string>();
                while (reader.Read())
                {
                    var sb = new StringBuilder();
                    for (var c = 0; c < reader.FieldCount; c++)
                    {
                        var name = reader.GetName(c);
                        sb.Append(name).Append('=');
                        sb.Append(reader.IsDBNull(c)
                            ? "<null>"
                            : Canonical(isStagingTable, name, System.Convert.ToString(reader.GetValue(c), CultureInfo.InvariantCulture)));
                        sb.Append('|');
                    }
                    rows.Add(sb.ToString());
                }
                rows.Sort();
                perTable.Add(rows);
            } while (reader.NextResult());

            return perTable;
        }

        private static string Canonical(bool isStagingTable, string column, string value)
        {
            if (!isStagingTable || !JsonValuedColumns.Contains(column) || string.IsNullOrWhiteSpace(value)) return value;
            return JToken.Parse(value).ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
