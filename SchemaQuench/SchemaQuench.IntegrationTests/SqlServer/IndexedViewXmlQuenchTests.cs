// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer
{
    // XML-ingest twin equivalence: SchemaSmith.IndexedViewXmlQuench (shredding the model with
    // .nodes()/.value() below the OPENJSON compat cliff) must converge the same indexed view +
    // index state as the JSON SchemaSmith.IndexedViewQuench. Runs on the modern container under
    // the standard main DB; the XML proc is loaded under a test-only name so it does not clobber
    // the kindled JSON proc.
    [TestFixture]
    [Category("SqlServer")]
    public class IndexedViewXmlQuenchTests : BaseTableQuenchTests
    {
        // Same view over the given name; a clustered + a nonclustered index with an INCLUDE column
        // exercises the DesiredIdx parse (block 1) and the CREATE INDEX builder (block 2).
        private static string PayloadJson(string view) => @"[{""Schema"":""[dbo]"",""Name"":""[" + view + @"]"",
            ""Definition"":""SELECT Id, Label, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM dbo.IvXmlSource GROUP BY Id, Label"",
            ""Indexes"":[
              {""Name"":""[UDX_" + view + @"_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""},
              {""Name"":""[IX_" + view + @"_Label]"",""Unique"":false,""Clustered"":false,""IndexColumns"":""[Label] DESC"",""IncludeColumns"":""[Cnt]""}
            ]}]";

        [Test]
        public void XmlIndexedView_ConvergesViewAndIndexesIdenticalTo_JsonIndexedView()
        {
            const string jv = "vIvEquivJson";
            const string xv = "vIvEquivXml";
            var xmlScript = (ResourceLoader.Load("SchemaSmith.IndexedViewXmlQuench.sql", Platform.SqlServer)
                             ?? throw new System.Exception("IndexedViewXmlQuench.sql not found"))
                            .Replace("[SchemaSmith].[IndexedViewQuench]", "[SchemaSmith].[IndexedViewQuenchXmlTest]");

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            Cleanup(conn, jv, xv);
            try
            {
                Exec(conn, "CREATE TABLE dbo.IvXmlSource (Id INT NOT NULL, Label VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL); " +
                           "CREATE UNIQUE CLUSTERED INDEX UDX_IvXmlSource ON dbo.IvXmlSource (Id);");
                foreach (var b in SqlServerBatchSplitter.Split(xmlScript)) Exec(conn, b);

                // Distinct product names: IndexedViewQuench drops product-owned views absent from the
                // payload, so a shared product would make the second run reap the first run's view.
                Exec(conn, "EXEC SchemaSmith.IndexedViewQuench @ProductName = 'IvEqJson', @IndexedViewSchema = @p, @TemplateName = N'Main', @SchemaName = N''",
                    ("@p", PayloadJson(jv)));
                Exec(conn, "EXEC SchemaSmith.IndexedViewQuenchXmlTest @ProductName = 'IvEqXml', @IndexedViewSchema = @p, @TemplateName = N'Main', @SchemaName = N''",
                    ("@p", ModelXmlSerializer.ToIngestXml(PayloadJson(xv), "IndexedViews", "IndexedView")));

                var jsonState = ViewState(conn, jv);
                var xmlState = ViewState(conn, xv);
                Assert.Multiple(() =>
                {
                    Assert.That(jsonState, Is.Not.Empty, "JSON IndexedViewQuench converged no indexes");
                    Assert.That(xmlState, Is.EqualTo(jsonState), "XML IndexedViewQuench view/index state differs from JSON path");
                });
            }
            finally
            {
                Cleanup(conn, jv, xv);
            }
        }

        private static void Cleanup(IDbConnection conn, string jv, string xv) =>
            Exec(conn, $"DROP VIEW IF EXISTS dbo.{jv}; DROP VIEW IF EXISTS dbo.{xv}; " +
                       "DROP TABLE IF EXISTS dbo.IvXmlSource; " +
                       "DROP PROCEDURE IF EXISTS SchemaSmith.IndexedViewQuenchXmlTest;");

        // Index signature excluding the name (which embeds the differing view name): the view must be
        // indexed, plus per index its flags + type + key columns (with sort direction) + include columns.
        private static List<string> ViewState(IDbConnection conn, string view)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT CONVERT(VARCHAR,OBJECTPROPERTY(i.object_id,'IsIndexed')) + '|' +
                CONVERT(VARCHAR,i.is_unique) + '|' + i.type_desc + '|K:' +
                ISNULL(STUFF((SELECT ',' + col.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                        FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                        ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, ''), '') + '|I:' +
                ISNULL(STUFF((SELECT ',' + col.name FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                        ORDER BY col.name FOR XML PATH('')), 1, 1, ''), '')
                FROM sys.indexes i WHERE i.object_id = OBJECT_ID('dbo.' + @v) AND i.type_desc <> 'HEAP'
                ORDER BY i.is_unique DESC, i.type_desc";
            AddParam(cmd, "@v", view);
            var rows = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(reader.GetString(0));
            return rows;
        }

        private static void Exec(IDbConnection conn, string sql, params (string name, string value)[] parameters)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = sql;
            foreach (var (n, v) in parameters) AddParam(cmd, n, v);
            TransientRetry.Run(() => cmd.ExecuteNonQuery());
        }

        private static void AddParam(IDbCommand cmd, string name, string value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            p.DbType = DbType.String;
            cmd.Parameters.Add(p);
        }
    }
}
