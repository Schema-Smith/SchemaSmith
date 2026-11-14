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

        // The JSON tier deployed an index's Description and the XML tier (2008 to 2016, or compatibility below 130) did
        // not, while extraction on that tier read it back: a declared description never arrived.
        private static string DescribedPayloadJson(string view, string description) => @"[{""Schema"":""[dbo]"",""Name"":""[" + view + @"]"",
            ""Definition"":""SELECT Id, Label, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM dbo.IvXmlSource GROUP BY Id, Label"",
            ""Indexes"":[
              {""Name"":""[UDX_" + view + @"_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""" +
            (description == null ? "" : @",""Description"":""" + description + @"""") + @"}
            ]}]";

        private static string ViewDescribedPayloadJson(string view, string description) => @"[{""Schema"":""[dbo]"",""Name"":""[" + view + @"]""" +
            (description == null ? "" : @",""Description"":""" + description + @"""") + @",
            ""Definition"":""SELECT Id, Label, COUNT_BIG(*) AS Cnt, SUM(Amount) AS TotalAmount FROM dbo.IvXmlSource GROUP BY Id, Label"",
            ""Indexes"":[{""Name"":""[UDX_" + view + @"_Id]"",""Unique"":true,""Clustered"":true,""IndexColumns"":""[Id]""}]}]";

        [Test]
        public void XmlIndexedView_TheViewsDescription_IsRemovedByAnEmptyString()
        {
            // The view-level Description read text() only, so "" looked omitted and the description stayed.
            const string xv = "vIvViewDescXml";
            var xmlScript = (ResourceLoader.Load("SchemaSmith.IndexedViewXmlQuench.sql", Platform.SqlServer)
                             ?? throw new System.Exception("IndexedViewXmlQuench.sql not found"))
                            .Replace("[SchemaSmith].[IndexedViewQuench]", "[SchemaSmith].[IndexedViewQuenchXmlTest]");

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            Cleanup(conn, xv, xv);
            try
            {
                Exec(conn, "CREATE TABLE dbo.IvXmlSource (Id INT NOT NULL, Label VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL); " +
                           "CREATE UNIQUE CLUSTERED INDEX UDX_IvXmlSource ON dbo.IvXmlSource (Id);");
                foreach (var b in SqlServerBatchSplitter.Split(xmlScript)) Exec(conn, b);

                string Deploy(string description)
                {
                    Exec(conn, "EXEC SchemaSmith.IndexedViewQuenchXmlTest @ProductName = 'IvViewDescXml', @IndexedViewSchema = @p, @TemplateName = N'Main', @SchemaName = N''",
                        ("@p", ModelXmlSerializer.ToIngestXml(ViewDescribedPayloadJson(xv, description), "IndexedViews", "IndexedView")));
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT CONVERT(NVARCHAR(MAX), ep.value) FROM sys.extended_properties ep WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = 'MS_Description' AND ep.major_id = OBJECT_ID('dbo.{xv}')";
                    return cmd.ExecuteScalar() as string;
                }

                Assert.That(Deploy("the view"), Is.EqualTo("the view"), "set on create");
                Assert.That(Deploy(null), Is.EqualTo("the view"), "omitted leaves it");
                Assert.That(Deploy(""), Is.Null, "an empty string removes it");
            }
            finally
            {
                Cleanup(conn, xv, xv);
            }
        }

        [Test]
        public void XmlIndexedView_AnIndexDescription_IsSet_LeftAloneWhenOmitted_AndRemovedByAnEmptyString()
        {
            const string xv = "vIvDescXml";
            var xmlScript = (ResourceLoader.Load("SchemaSmith.IndexedViewXmlQuench.sql", Platform.SqlServer)
                             ?? throw new System.Exception("IndexedViewXmlQuench.sql not found"))
                            .Replace("[SchemaSmith].[IndexedViewQuench]", "[SchemaSmith].[IndexedViewQuenchXmlTest]");

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            Cleanup(conn, xv, xv);
            try
            {
                Exec(conn, "CREATE TABLE dbo.IvXmlSource (Id INT NOT NULL, Label VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL); " +
                           "CREATE UNIQUE CLUSTERED INDEX UDX_IvXmlSource ON dbo.IvXmlSource (Id);");
                foreach (var b in SqlServerBatchSplitter.Split(xmlScript)) Exec(conn, b);

                string Deploy(string description)
                {
                    Exec(conn, "EXEC SchemaSmith.IndexedViewQuenchXmlTest @ProductName = 'IvDescXml', @IndexedViewSchema = @p, @TemplateName = N'Main', @SchemaName = N''",
                        ("@p", ModelXmlSerializer.ToIngestXml(DescribedPayloadJson(xv, description), "IndexedViews", "IndexedView")));
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT CONVERT(NVARCHAR(MAX), ep.value) FROM sys.extended_properties ep JOIN sys.indexes i ON i.[object_id] = ep.major_id AND i.index_id = ep.minor_id " +
                                      $"WHERE ep.class = 7 AND ep.name = 'MS_Description' AND ep.major_id = OBJECT_ID('dbo.{xv}') AND i.name = 'UDX_{xv}_Id'";
                    return cmd.ExecuteScalar() as string;
                }

                Assert.That(Deploy("by id"), Is.EqualTo("by id"), "set on create");
                Assert.That(Deploy(null), Is.EqualTo("by id"), "omitted means the description is not managed, so the database's stays");
                Assert.That(Deploy(""), Is.Null, "an empty string is the request to remove it");
            }
            finally
            {
                Cleanup(conn, xv, xv);
            }
        }

        // The refusal is the XML tier's, so it must CREATE on SQL Server 2008: THROW (2012) failed the whole kindle there.
        [Test]
        public void XmlIndexedView_AnUnownedViewOfTheSameName_IsRefusedByName_AndLeftAsItWas()
        {
            const string xv = "vIvCollideXml";
            var xmlScript = (ResourceLoader.Load("SchemaSmith.IndexedViewXmlQuench.sql", Platform.SqlServer)
                             ?? throw new System.Exception("IndexedViewXmlQuench.sql not found"))
                            .Replace("[SchemaSmith].[IndexedViewQuench]", "[SchemaSmith].[IndexedViewQuenchXmlTest]");

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            Cleanup(conn, xv, xv);
            try
            {
                Exec(conn, "CREATE TABLE dbo.IvXmlSource (Id INT NOT NULL, Label VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL); " +
                           "CREATE UNIQUE CLUSTERED INDEX UDX_IvXmlSource ON dbo.IvXmlSource (Id);");
                Exec(conn, $"CREATE VIEW dbo.{xv} AS SELECT Id FROM dbo.IvXmlSource");
                foreach (var b in SqlServerBatchSplitter.Split(xmlScript)) Exec(conn, b);

                var ex = Assert.Catch<Microsoft.Data.SqlClient.SqlException>(() =>
                    Exec(conn, "EXEC SchemaSmith.IndexedViewQuenchXmlTest @ProductName = 'IvCollideXml', @IndexedViewSchema = @p, @TemplateName = N'Main', @SchemaName = N''",
                        ("@p", ModelXmlSerializer.ToIngestXml(PayloadJson(xv), "IndexedViews", "IndexedView"))));

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT CONVERT(VARCHAR, OBJECTPROPERTY(OBJECT_ID('dbo.{xv}'), 'IsView')) + '|' + CONVERT(VARCHAR, OBJECTPROPERTY(OBJECT_ID('dbo.{xv}'), 'IsIndexed'))";
                var state = cmd.ExecuteScalar() as string;
                Assert.Multiple(() =>
                {
                    Assert.That(ex!.Message, Does.Contain(xv).And.Contain("not owned"), ex.Message);
                    Assert.That(ex.Errors.Count, Is.EqualTo(1), $"the refusal stops the procedure: {ex.Message}");
                    Assert.That(state, Is.EqualTo("1|0"), "the unowned view is still there, unindexed");
                });
            }
            finally
            {
                Cleanup(conn, xv, xv);
            }
        }

        // The XML tier's preview prints the statements as the JSON tier's does.
        [Test]
        public void XmlIndexedView_WhatIf_PrintsTheStatementsTheDeployWouldRun()
        {
            const string xv = "vIvWhatIfXml";
            var xmlScript = (ResourceLoader.Load("SchemaSmith.IndexedViewXmlQuench.sql", Platform.SqlServer)
                             ?? throw new System.Exception("IndexedViewXmlQuench.sql not found"))
                            .Replace("[SchemaSmith].[IndexedViewQuench]", "[SchemaSmith].[IndexedViewQuenchXmlTest]");
            var messages = new List<string>();
            using var conn = (Microsoft.Data.SqlClient.SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.InfoMessage += (_, e) => { foreach (Microsoft.Data.SqlClient.SqlError err in e.Errors) messages.Add(err.Message); };
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            Cleanup(conn, "vIvWhatIfUnused", xv);
            try
            {
                Exec(conn, "CREATE TABLE dbo.IvXmlSource (Id INT NOT NULL, Label VARCHAR(100) NOT NULL, Amount DECIMAL(10,2) NOT NULL); " +
                           "CREATE UNIQUE CLUSTERED INDEX UDX_IvXmlSource ON dbo.IvXmlSource (Id);");
                foreach (var b in SqlServerBatchSplitter.Split(xmlScript)) Exec(conn, b);
                const string run = "EXEC SchemaSmith.IndexedViewQuenchXmlTest @ProductName = 'IvWhatIfXml', @IndexedViewSchema = @p, @WhatIf = {0}, @TemplateName = N'Main', @SchemaName = N''";

                messages.Clear();
                Exec(conn, string.Format(run, 1), ("@p", ModelXmlSerializer.ToIngestXml(PayloadJson(xv), "IndexedViews", "IndexedView")));
                var created = new List<string>(messages);
                Exec(conn, string.Format(run, 0), ("@p", ModelXmlSerializer.ToIngestXml(PayloadJson(xv), "IndexedViews", "IndexedView")));
                messages.Clear();
                Exec(conn, string.Format(run, 1), ("@p", ModelXmlSerializer.ToIngestXml("[]", "IndexedViews", "IndexedView")));
                var removed = new List<string>(messages);

                Assert.Multiple(() =>
                {
                    Assert.That(created, Has.Some.StartsWith($"CREATE VIEW [dbo].[{xv}] WITH SCHEMABINDING AS SELECT"));
                    Assert.That(removed, Has.Some.EqualTo($"DROP INDEX [IX_{xv}_Label] ON [dbo].[{xv}]"));
                    Assert.That(removed, Has.Some.EqualTo($"DROP VIEW [dbo].[{xv}]"));
                    Assert.That(ViewState(conn, xv), Is.Not.Empty, "a preview dropped the view");
                });
            }
            finally
            {
                Cleanup(conn, "vIvWhatIfUnused", xv);
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
