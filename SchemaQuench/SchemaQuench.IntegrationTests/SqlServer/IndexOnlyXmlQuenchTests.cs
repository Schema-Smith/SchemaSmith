// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Data;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.SqlServer
{
    [TestFixture]
    [Category("SqlServer")]
    public class IndexOnlyXmlQuenchTests : BaseTableQuenchTests
    {
        private static string PayloadJson(string table) => @"[{""Schema"":""dbo"",""Name"":""" + table + @""",
            ""Indexes"":[
              {""Name"":""PK_" + table + @""",""PrimaryKey"":true,""Unique"":true,""Clustered"":true,""IndexColumns"":""Id""},
              {""Name"":""IX_" + table + @"_Label"",""Unique"":false,""Clustered"":false,""IndexColumns"":""Label DESC"",""IncludeColumns"":""Id""}
            ]}]";

        [Test]
        public void XmlIndexOnly_ConvergesIndexesIdenticalTo_JsonIndexOnly()
        {
            const string jt = "IdxOnlyEquivJson";
            const string xt = "IdxOnlyEquivXml";
            var xmlScript = XmlTierIndexOnlyQuench();

            using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
            conn.Open();
            conn.ChangeDatabase(_mainDb);

            Cleanup(conn, jt, xt);
            try
            {
                Exec(conn, $"CREATE TABLE dbo.{jt} (Id INT NOT NULL, Label NVARCHAR(100) NULL);");
                Exec(conn, $"CREATE TABLE dbo.{xt} (Id INT NOT NULL, Label NVARCHAR(100) NULL);");
                foreach (var b in SqlServerBatchSplitter.Split(xmlScript)) Exec(conn, b);

                Exec(conn, "EXEC SchemaSmith.IndexOnlyQuench @ProductName = 'EqTest', @TableDefinitions = @p",
                    ("@p", PayloadJson(jt)));
                Exec(conn, "EXEC SchemaSmith.IndexOnlyQuenchXmlTest @ProductName = 'EqTest', @TableDefinitions = @p",
                    ("@p", ModelXmlSerializer.ToIngestXml(PayloadJson(xt), "Tables", "Table")));

                var jsonIdx = Indexes(conn, jt);
                var xmlIdx = Indexes(conn, xt);
                Assert.Multiple(() =>
                {
                    Assert.That(jsonIdx, Is.Not.Empty, "JSON IndexOnly converged no indexes");
                    Assert.That(xmlIdx, Is.EqualTo(jsonIdx), "XML IndexOnly indexes differ from JSON IndexOnly");
                });
            }
            finally
            {
                Cleanup(conn, jt, xt);
            }
        }

        // The XML-tier procedure, kindled as the product kindles it, under a test name beside the JSON one.
        internal static string XmlTierIndexOnlyQuench() =>
            ForgeKindler.ResolveKindleScript("SchemaSmith.IndexOnlyXmlQuench.sql", Platform.SqlServer, replaceParseJson: false, replaceTableDef: false)
                .Replace("SchemaSmith.IndexOnlyQuench", "SchemaSmith.IndexOnlyQuenchXmlTest");

        private static void Cleanup(IDbConnection conn, string jt, string xt) =>
            Exec(conn, $"DROP TABLE IF EXISTS dbo.{jt}; DROP TABLE IF EXISTS dbo.{xt}; " +
                       "DROP PROCEDURE IF EXISTS SchemaSmith.IndexOnlyQuenchXmlTest;");

        // Index signature excluding the name (which embeds the differing table name): flags + type + key
        // columns (with sort direction) + include columns.
        private static List<string> Indexes(IDbConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT CONVERT(VARCHAR,i.is_primary_key) + '|' + CONVERT(VARCHAR,i.is_unique) + '|' + i.type_desc + '|K:' +
                ISNULL(STUFF((SELECT ',' + col.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                        FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                        ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, ''), '') + '|I:' +
                ISNULL(STUFF((SELECT ',' + col.name FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                        ORDER BY col.name FOR XML PATH('')), 1, 1, ''), '')
                FROM sys.indexes i WHERE i.object_id = OBJECT_ID('dbo.' + @t) AND i.type_desc <> 'HEAP'
                ORDER BY i.is_primary_key DESC, i.is_unique DESC, i.type_desc";
            AddParam(cmd, "@t", table);
            var rows = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(reader.GetString(0));
            return rows;
        }

        private static void Exec(IDbConnection conn, string sql, params (string name, string value)[] parameters)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in parameters) AddParam(cmd, n, v);
            cmd.ExecuteNonQuery();
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
