// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using Schema.DataAccess;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

[Category("SqlServer")]
[Parallelizable(scope: ParallelScope.Self)]
public class IndexOnlyQuench_FullTextVariantTests : BaseTableQuenchTests
{
    private string TableJson(string tableName, string fullTextJson) => $$"""
        [{
            "Schema": "[dbo]",
            "Name": "[{{tableName}}]",
            "Indexes": [ { "Name": "[UDX_{{tableName}}]", "IndexColumns": "[Id]", "Unique": true } ],
            "FullTextIndex": {{fullTextJson}}
        }]
        """;

    private static void CreateTestTable(DbCommand cmd, string tableName)
    {
        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}; " +
                          $"CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Title VARCHAR(200) NULL); " +
                          $"CREATE UNIQUE INDEX UDX_{tableName} ON dbo.{tableName} (Id);";
        cmd.ExecuteNonQuery();
    }

    private void RunIndexOnlyQuench(DbCommand cmd, string tableName, string fullTextJson, bool whatIf = false)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.IndexOnlyQuench @ProductName = @p, @TableDefinitions = @t, @WhatIf = @w";
        cmd.Parameters.Clear();
        var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = _productName; cmd.Parameters.Add(p);
        var t = cmd.CreateParameter(); t.ParameterName = "@t"; t.Value = TableJson(tableName, fullTextJson); cmd.Parameters.Add(t);
        var w = cmd.CreateParameter(); w.ParameterName = "@w"; w.Value = whatIf; cmd.Parameters.Add(w);
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        cmd.Parameters.Clear();
    }

    /// <summary>
    /// Whether this server can create a STATISTICAL_SEMANTICS full-text column at all, which requires a
    /// Semantic Language Statistics Database to be attached AND registered.
    /// </summary>
    private static bool SemanticsAvailable(DbCommand cmd)
    {
        cmd.CommandText = "SELECT COUNT(*) FROM sys.fulltext_semantic_language_statistics_database";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static int DeployedSemanticColumns(DbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.fulltext_index_columns fc WITH (NOLOCK)
  WHERE fc.[object_id] = OBJECT_ID('dbo.{tableName}') AND fc.statistical_semantics = 1";
        return (int)cmd.ExecuteScalar();
    }

    // Completes the per-column full-text trio; TYPE COLUMN and LANGUAGE were already supported.
    // Needs the Semantic Language Statistics Database attached and registered — scripts/provision-semantic-db.sh
    // does that and CI runs it, so a failure mentioning "semantic functionality is not available" means the
    // database is missing rather than the feature being broken.
    [Test]
    public void FullText_StatisticalSemantics_DeploysAndIsIdempotent()
    {
        const string tableName = "FtSemantics";
        using var conn = (DbConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        // Guarded on the capability rather than the server version: this must still RUN anywhere the
        // database is registered, and a version check would also skip a 2017 that had been provisioned
        // by hand. On the 2017 container it cannot be provisioned at all -- Microsoft's mssql-server-fts
        // package ships a semanticsdb.bak stamped 14.00.0700 (RTM) while the image runs 14.00.3540, and
        // the restore is refused with Msg 3169 "backed up on a server running version ... incompatible".
        // The backup that ships with the server cannot be restored into that server, so the feature is
        // unreachable there through no fault of the code under test.
        if (!SemanticsAvailable(cmd))
            Assert.Ignore(
                "No Semantic Language Statistics Database is registered on this server, so a "
                + "STATISTICAL_SEMANTICS column cannot be created and this feature cannot be exercised. "
                + "On the SQL Server 2017 container this is expected and unfixable locally: the bundled "
                + "semanticsdb.bak is stamped 14.00.0700 while the engine is 14.00.3540, and SQL Server "
                + "refuses the restore (Msg 3169). The feature IS covered on 2019/2022/2025, where "
                + "scripts/provision-semantic-db.sh succeeds -- if this skips THERE, provisioning "
                + "regressed and the skip is hiding a real failure.");

        CreateTestTable(cmd, tableName);

        // ChangeTracking is NOT optional here. The emitter concatenates it into the CREATE statement and
        // T-SQL concatenation with NULL yields NULL, so omitting it makes the whole statement vanish and no
        // index is created — silently, with no error and no log line. StopList beside it is COALESCE-guarded;
        // this one is not.
        var ftJson = $@"{{ ""FullTextCatalog"": ""[FT_Catalog]"", ""KeyIndex"": ""[UDX_{tableName}]"",
                           ""Columns"": ""[Title] LANGUAGE 1033 STATISTICAL_SEMANTICS"", ""ChangeTracking"": ""AUTO"" }}";
        RunIndexOnlyQuench(cmd, tableName, ftJson);

        Assert.That(DeployedSemanticColumns(cmd, tableName), Is.EqualTo(1),
            "the declared STATISTICAL_SEMANTICS must reach the deployed index");

        // The reason it is rendered on BOTH the catalog and model sides: a clause emitted by only one of them
        // makes declared != live forever, so every deploy drops and repopulates the index. A full-text
        // rebuild is a full repopulation, so that is expensive churn rather than a redundant statement.
        RunIndexOnlyQuench(cmd, tableName, ftJson);
        Assert.That(DeployedSemanticColumns(cmd, tableName), Is.EqualTo(1),
            "a second identical deploy must leave the index alone");

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    // ChangeTracking concatenates into the CREATE FULLTEXT INDEX statement, and T-SQL concatenation with
    // NULL yields NULL -- so a null value made the whole statement NULL and NO index was created, silently:
    // no error, no log line, and the next deploy would try again and fail the same way. The C# default is
    // "AUTO", but an explicit null in a table file overwrites a property initializer, so this was reachable
    // from an ordinary package rather than only by calling the proc by hand.
    [Test]
    public void FullText_NullChangeTracking_StillCreatesTheIndex()
    {
        const string tableName = "FtNullTracking";
        using var conn = (DbConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = conn.CreateCommand();
        CreateTestTable(cmd, tableName);

        RunIndexOnlyQuench(cmd, tableName,
            $$"""{ "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_{{tableName}}]", "Columns": "[Title]", "ChangeTracking": null }""");

        cmd.CommandText = $"SELECT COUNT(*) FROM sys.fulltext_indexes WITH (NOLOCK) WHERE [object_id] = OBJECT_ID('dbo.{tableName}')";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1),
            "a null ChangeTracking must fall back to the default, not silently skip the whole index");

        cmd.CommandText = $@"
SELECT fi.change_tracking_state_desc FROM sys.fulltext_indexes fi WITH (NOLOCK)
  WHERE fi.[object_id] = OBJECT_ID('dbo.{tableName}')";
        Assert.That(cmd.ExecuteScalar()?.ToString(), Is.EqualTo("AUTO"),
            "the fallback must match the C# default so omitted and explicitly-null behave the same");

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    private static string DeployedCatalog(DbCommand cmd, string tableName)
    {
        cmd.CommandText = $@"
SELECT c.[name] FROM sys.fulltext_indexes fi WITH (NOLOCK)
  JOIN sys.fulltext_catalogs c WITH (NOLOCK) ON c.fulltext_catalog_id = fi.fulltext_catalog_id
  WHERE fi.[object_id] = OBJECT_ID('dbo.{tableName}')";
        return cmd.ExecuteScalar() as string;
    }

    private static int? DeployedLanguageId(DbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT fc.language_id FROM sys.fulltext_index_columns fc WITH (NOLOCK)
  WHERE fc.[object_id] = OBJECT_ID('dbo.{tableName}')
    AND fc.column_id = COLUMNPROPERTY(OBJECT_ID('dbo.{tableName}'), '{columnName}', 'ColumnId')";
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? (int?)null : Convert.ToInt32(result);
    }

    private static string DeployedTypeColumn(DbCommand cmd, string tableName, string columnName)
    {
        cmd.CommandText = $@"
SELECT COL_NAME(fc.[object_id], fc.type_column_id) FROM sys.fulltext_index_columns fc WITH (NOLOCK)
  WHERE fc.[object_id] = OBJECT_ID('dbo.{tableName}')
    AND fc.column_id = COLUMNPROPERTY(OBJECT_ID('dbo.{tableName}'), '{columnName}', 'ColumnId')";
        return cmd.ExecuteScalar() as string;
    }

    // Full-text LANGUAGE churn: a declared per-column LANGUAGE previously could never compare equal to
    // the live index -- neither extraction nor the live-side comparison build rendered
    // sys.fulltext_index_columns.language_id -- so a package that declared one rebuilt (dropped and
    // recreated, paying a full repopulation) its full-text index on every single deploy.
    [Test]
    public void FullText_ExplicitNonDefaultLanguage_Deploys()
    {
        const string tableName = "FtLang_Explicit";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        RunIndexOnlyQuench(cmd, tableName,
            """{ "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_FtLang_Explicit]", "Columns": "[Title] LANGUAGE 1041", "ChangeTracking": "OFF" }""");

        Assert.That(DeployedLanguageId(cmd, tableName, "Title"), Is.EqualTo(1041),
            "a declared per-column LANGUAGE must reach CREATE FULLTEXT INDEX and deploy as declared");
        conn.Close();
    }

    [Test]
    public void FullText_ExplicitLanguage_RequenchPerformsNoFullTextWork()
    {
        const string tableName = "FtLang_NoChurn";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        var json = """{ "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_FtLang_NoChurn]", "Columns": "[Title] LANGUAGE 1041", "ChangeTracking": "OFF" }""";
        RunIndexOnlyQuench(cmd, tableName, json);
        Assert.That(DeployedLanguageId(cmd, tableName, "Title"), Is.EqualTo(1041));

        var captured = new StringBuilder();
        var sqlConn = (SqlConnection)conn;
        SqlInfoMessageEventHandler handler = (_, e) => captured.Append(e.Message);
        sqlConn.InfoMessage += handler;
        try
        {
            RunIndexOnlyQuench(cmd, tableName, json, whatIf: true);
        }
        finally
        {
            sqlConn.InfoMessage -= handler;
        }

        // The regression this closes: before the fix, neither side ever rendered LANGUAGE, so a
        // declared value could never compare equal to live and the index dropped/recreated every deploy.
        Assert.That(captured.ToString(), Does.Not.Contain("FULLTEXT"),
            "a declared LANGUAGE that already matches the live index must not be seen as drift on the next deploy");
        conn.Close();
    }

    // A full-text index's columns are a set. Declared out of alphabetical order they compared unequal to the catalog's
    // sorted list, so every deploy dropped the index and repopulated it; a real column change must still be seen.
    [TestCase(false)]
    [TestCase(true)]
    public void FullText_ColumnsDeclaredOutOfOrder_RedeployDoesNoFullTextWork_AndAColumnChangeStillApplies(bool indexOnly)
    {
        var tableName = indexOnly ? "FtOrder_IndexOnly" : "FtOrder_Main";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName};";
        cmd.ExecuteNonQuery();
        string Json(string columns) => $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[{{tableName}}]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Title]", "DataType": "VARCHAR(200)", "Nullable": true },
                    { "Name": "[Body]", "DataType": "VARCHAR(200)", "Nullable": true }
                ],
                "Indexes": [ { "Name": "[UDX_{{tableName}}]", "IndexColumns": "[Id]", "Unique": true } ],
                "FullTextIndex": { "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_{{tableName}}]", "Columns": "{{columns}}", "ChangeTracking": "OFF" }
            }]
            """;
        string WhatIf(string columns)
        {
            var captured = new StringBuilder();
            SqlInfoMessageEventHandler handler = (_, e) => captured.Append(e.Message);
            ((SqlConnection)conn).InfoMessage += handler;
            try { RunTableQuenchProc(cmd, Json(columns), indexOnly: indexOnly, whatIf: true); }
            finally { ((SqlConnection)conn).InfoMessage -= handler; }
            return captured.ToString();
        }

        try
        {
            if (indexOnly) RunTableQuenchProc(cmd, Json("[Title],[Body]"));
            RunTableQuenchProc(cmd, Json("[Title],[Body]"), indexOnly: indexOnly);

            Assert.Multiple(() =>
            {
                Assert.That(WhatIf("[Title],[Body]"), Does.Not.Contain("Dropping fulltext index"), "the same columns in another order are not a change");
                Assert.That(WhatIf("[Title]"), Does.Contain("Dropping fulltext index"), "a column removed from the index still is");
            });
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName};";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    // The XML-ingest index-only procedure carries its own copy of the full-text column compare.
    [Test]
    public void FullText_ColumnsDeclaredOutOfOrder_XmlIndexOnly_RedeployDoesNoFullTextWork_AndAColumnChangeStillApplies()
    {
        const string tableName = "FtOrder_XmlIndexOnly";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();
        string Json(string columns) => $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[{{tableName}}]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Title]", "DataType": "VARCHAR(200)", "Nullable": true },
                    { "Name": "[Body]", "DataType": "VARCHAR(200)", "Nullable": true }
                ],
                "Indexes": [ { "Name": "[UDX_{{tableName}}]", "IndexColumns": "[Id]", "Unique": true } ],
                "FullTextIndex": { "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_{{tableName}}]", "Columns": "{{columns}}", "ChangeTracking": "OFF" }
            }]
            """;
        string XmlIndexOnly(string columns)
        {
            var captured = new StringBuilder();
            SqlInfoMessageEventHandler handler = (_, e) => captured.Append(e.Message);
            ((SqlConnection)conn).InfoMessage += handler;
            try
            {
                cmd.CommandText = "EXEC SchemaSmith.IndexOnlyQuenchXmlFtTest @ProductName = @n, @TableDefinitions = @p, @DropUnknownIndexes = 1";
                cmd.Parameters.Clear();
                cmd.Parameters.Add(new SqlParameter("@n", _productName));
                cmd.Parameters.Add(new SqlParameter("@p", Schema.Utility.ModelXmlSerializer.ToIngestXml(Json(columns), "Tables", "Table")));
                cmd.ExecuteNonQuery();
            }
            finally
            {
                cmd.Parameters.Clear();
                ((SqlConnection)conn).InfoMessage -= handler;
            }
            return captured.ToString();
        }

        try
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName};";
            cmd.ExecuteNonQuery();
            RunTableQuenchProc(cmd, Json("[Title],[Body]"));
            foreach (var batch in SqlServerBatchSplitter.Split(IndexOnlyXmlQuenchTests.XmlTierIndexOnlyQuench().Replace("IndexOnlyQuenchXmlTest", "IndexOnlyQuenchXmlFtTest")))
            {
                cmd.CommandText = batch;
                cmd.ExecuteNonQuery();
            }

            Assert.That(XmlIndexOnly("[Title],[Body]"), Does.Not.Contain("Dropping fulltext index"), "the same columns in another order are not a change");
            Assert.That(XmlIndexOnly("[Title]"), Does.Contain("Dropping fulltext index"), "a column removed from the index still is");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}; DROP PROCEDURE IF EXISTS SchemaSmith.IndexOnlyQuenchXmlFtTest;";
            cmd.ExecuteNonQuery();
        }
    }

    [Test]
    public void FullText_TypeColumnAndLanguage_KeepsBoth()
    {
        const string tableName = "FtLang_TypeColumn";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}; " +
                          $"CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Doc VARBINARY(MAX) NULL, DocType NVARCHAR(4) NULL); " +
                          $"CREATE UNIQUE INDEX UDX_{tableName} ON dbo.{tableName} (Id);";
        cmd.ExecuteNonQuery();

        RunIndexOnlyQuench(cmd, tableName,
            """{ "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_FtLang_TypeColumn]", "Columns": "[Doc] TYPE COLUMN [DocType] LANGUAGE 1041", "ChangeTracking": "OFF" }""");

        Assert.Multiple(() =>
        {
            Assert.That(DeployedTypeColumn(cmd, tableName, "Doc"), Is.EqualTo("DocType"), "TYPE COLUMN must still be honored alongside LANGUAGE");
            Assert.That(DeployedLanguageId(cmd, tableName, "Doc"), Is.EqualTo(1041), "LANGUAGE must be honored alongside TYPE COLUMN");
        });
        conn.Close();
    }

    [Test]
    public void FullText_NoExplicitLanguage_IsCompletelyUnaffected()
    {
        // Backward-compatibility guard: an ordinary full-text index with no declared LANGUAGE -- the
        // state every existing package is in -- must not churn on requench.
        const string tableName = "FtLang_Default";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        var json = """{ "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_FtLang_Default]", "Columns": "[Title]", "ChangeTracking": "OFF" }""";
        RunIndexOnlyQuench(cmd, tableName, json);

        var captured = new StringBuilder();
        var sqlConn = (SqlConnection)conn;
        SqlInfoMessageEventHandler handler = (_, e) => captured.Append(e.Message);
        sqlConn.InfoMessage += handler;
        try
        {
            RunIndexOnlyQuench(cmd, tableName, json, whatIf: true);
        }
        finally
        {
            sqlConn.InfoMessage -= handler;
        }

        Assert.That(captured.ToString(), Does.Not.Contain("FULLTEXT"),
            "a full-text index with no declared LANGUAGE must not churn on requench");
        conn.Close();
    }

    [Test]
    public void FullText_ExplicitLanguage_MainPathRequenchPerformsNoFullTextWork()
    {
        // Same regression as FullText_ExplicitLanguage_RequenchPerformsNoFullTextWork above, but through
        // the ordinary deploy path (SchemaSmith.TableQuench / SchemaSmith.ModifiedTableQuench) that almost
        // every user runs -- ModifiedTableQuench.sql has its own separate copy of the live-side Columns
        // build, so fixing only IndexOnlyQuench.sql left this path -- the common case -- still churning on
        // every deploy.
        const string tableName = "FtLang_MainPathNoChurn";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName};";
        cmd.ExecuteNonQuery();

        var json = $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[{{tableName}}]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Title]", "DataType": "VARCHAR(200)", "Nullable": true }
                ],
                "Indexes": [ { "Name": "[UDX_{{tableName}}]", "IndexColumns": "[Id]", "Unique": true } ],
                "FullTextIndex": { "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_{{tableName}}]", "Columns": "[Title] LANGUAGE 1041", "ChangeTracking": "OFF" }
            }]
            """;

        try
        {
            RunTableQuenchProc(cmd, json);
            Assert.That(DeployedLanguageId(cmd, tableName, "Title"), Is.EqualTo(1041));

            var captured = new StringBuilder();
            var sqlConn = (SqlConnection)conn;
            SqlInfoMessageEventHandler handler = (_, e) => captured.Append(e.Message);
            sqlConn.InfoMessage += handler;
            try
            {
                RunTableQuenchProc(cmd, json, whatIf: true);
            }
            finally
            {
                sqlConn.InfoMessage -= handler;
            }

            Assert.That(captured.ToString(), Does.Not.Contain("FULLTEXT"),
                "a declared LANGUAGE that already matches the live index must not be seen as drift on the ordinary deploy path either");
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName};";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    [Test]
    public void FullText_SingleObjectShape_StillDeploys()
    {
        const string tableName = "FtVariant_BackCompat";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        RunIndexOnlyQuench(cmd, tableName,
            """{ "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_FtVariant_BackCompat]", "Columns": "[Title]", "ChangeTracking": "OFF" }""");

        Assert.That(DeployedCatalog(cmd, tableName), Is.EqualTo("FT_Catalog"));
        conn.Close();
    }

    [Test]
    public void FullText_VariantArray_SelectsTheMatchingVariant()
    {
        const string tableName = "FtVariant_Select";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        RunIndexOnlyQuench(cmd, tableName, TwoVariantJson(tableName, _mainDb, "FtNoSuchDb"));

        Assert.That(DeployedCatalog(cmd, tableName), Is.EqualTo("FT_Catalog"));
        conn.Close();
    }

    [Test]
    public void FullText_RequenchWithMatchingVariant_PerformsNoFullTextWork()
    {
        const string tableName = "FtVariant_NoChurn";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        var json = TwoVariantJson(tableName, _mainDb, "FtNoSuchDb");
        RunIndexOnlyQuench(cmd, tableName, json);
        Assert.That(DeployedCatalog(cmd, tableName), Is.EqualTo("FT_Catalog"));

        var captured = new StringBuilder();
        var sqlConn = (SqlConnection)conn;
        SqlInfoMessageEventHandler handler = (_, e) => captured.Append(e.Message);
        sqlConn.InfoMessage += handler;
        try
        {
            RunIndexOnlyQuench(cmd, tableName, json, whatIf: true);
        }
        finally
        {
            sqlConn.InfoMessage -= handler;
        }

        Assert.That(captured.ToString(), Does.Not.Contain("FULLTEXT"));
        conn.Close();
    }

    [Test]
    public void FullText_VariantSwitch_ConvergesToNewlySelectedVariant()
    {
        const string tableName = "FtVariant_Switch";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        cmd.CommandText = "DROP TABLE IF EXISTS dbo.FtVariant_SwitchGate; " +
                          "CREATE TABLE dbo.FtVariant_SwitchGate (ActiveCatalog VARCHAR(50)); " +
                          "INSERT INTO dbo.FtVariant_SwitchGate (ActiveCatalog) VALUES ('FT_Catalog');";
        cmd.ExecuteNonQuery();

        var json = GatedTwoVariantJson(tableName,
            "EXISTS (SELECT 1 FROM dbo.FtVariant_SwitchGate WHERE ActiveCatalog = 'FT_Catalog')",
            "EXISTS (SELECT 1 FROM dbo.FtVariant_SwitchGate WHERE ActiveCatalog = 'FT_Catalog2')");

        RunIndexOnlyQuench(cmd, tableName, json);
        Assert.That(DeployedCatalog(cmd, tableName), Is.EqualTo("FT_Catalog"));

        cmd.CommandText = "UPDATE dbo.FtVariant_SwitchGate SET ActiveCatalog = 'FT_Catalog2';";
        cmd.ExecuteNonQuery();

        RunIndexOnlyQuench(cmd, tableName, json);
        Assert.That(DeployedCatalog(cmd, tableName), Is.EqualTo("FT_Catalog2"));
        conn.Close();
    }

    [Test]
    public void FullText_NoVariantMatches_DropsExistingIndex()
    {
        const string tableName = "FtVariant_ZeroMatch";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        cmd.CommandText = $"CREATE FULLTEXT INDEX ON dbo.{tableName} (Title) KEY INDEX UDX_{tableName} ON FT_Catalog WITH CHANGE_TRACKING = OFF";
        cmd.ExecuteNonQuery();

        RunIndexOnlyQuench(cmd, tableName, GatedTwoVariantJson(tableName, "1 = 0", "1 = 0"));

        Assert.That(DeployedCatalog(cmd, tableName), Is.Null);
        conn.Close();
    }

    [Test]
    public void FullText_MultipleVariantsMatch_FailsWithMutuallyExclusiveError()
    {
        const string tableName = "FtVariant_DoubleMatch";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);

        var ex = Assert.Throws<SqlException>(() =>
            RunIndexOnlyQuench(cmd, tableName, GatedTwoVariantJson(tableName, "1 = 1", "1 = 1")));
        Assert.That(ex!.Message, Does.Contain("mutually exclusive"));
        conn.Close();
    }

    [Test]
    public void FullText_MultipleVariantsMatch_FailsThroughSharedParser()
    {
        // The shared JSON parser (kindled into the modular table-quench procs via the {{ParseJson}}
        // token) carries the same multi-variant THROW. Exercise it through SchemaSmith.TableQuench.
        const string tableName = "FtVariant_SharedParser";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);

        var json = $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[{{tableName}}]",
                "Columns": [
                    { "Name": "[Id]", "DataType": "INT", "Nullable": false },
                    { "Name": "[Title]", "DataType": "VARCHAR(200)", "Nullable": true }
                ],
                "Indexes": [ { "Name": "[UDX_{{tableName}}]", "IndexColumns": "[Id]", "Unique": true } ],
                "FullTextIndex": {{GatedTwoVariantJson(tableName, "1 = 1", "1 = 1")}}
            }]
            """;

        var ex = Assert.Throws<SqlException>(() =>
        {
            cmd.CommandTimeout = 300;
            cmd.CommandText = "EXEC SchemaSmith.TableQuench @ProductName = @p, @TableDefinitions = @t, @DropTablesRemovedFromProduct = 0, @DropUnknownIndexes = 0";
            cmd.Parameters.Clear();
            var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = _productName; cmd.Parameters.Add(p);
            var t = cmd.CreateParameter(); t.ParameterName = "@t"; t.Value = json; cmd.Parameters.Add(t);
            TransientRetry.Run(() => cmd.ExecuteNonQuery());
            cmd.Parameters.Clear();
        });
        Assert.That(ex!.Message, Does.Contain("mutually exclusive"));
        conn.Close();
    }

    [Test]
    public void FullText_GatedOutSingleDeclaration_IsNotDeployed_IndexOnlyMode()
    {
        const string tableName = "FtVariant_GatedOut";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        CreateTestTable(cmd, tableName);
        RunIndexOnlyQuench(cmd, tableName,
            """{ "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_FtVariant_GatedOut]", "Columns": "[Title]", "ChangeTracking": "OFF", "ShouldApplyExpression": "1 = 0" }""");

        Assert.That(DeployedCatalog(cmd, tableName), Is.Null);
        conn.Close();
    }

    private static bool IndexExists(DbCommand cmd, string tableName, string indexName)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.indexes WITH (NOLOCK)
  WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = '{indexName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool StatisticExists(DbCommand cmd, string tableName, string statisticName)
    {
        cmd.CommandText = $@"
SELECT COUNT(*) FROM sys.stats WITH (NOLOCK)
  WHERE [object_id] = OBJECT_ID('dbo.{tableName}') AND [name] = '{statisticName}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private void RunIndexOnlyQuenchJson(DbCommand cmd, string json)
    {
        cmd.CommandTimeout = 300;
        cmd.CommandText = "EXEC SchemaSmith.IndexOnlyQuench @ProductName = @p, @TableDefinitions = @t, @WhatIf = 0";
        cmd.Parameters.Clear();
        var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = _productName; cmd.Parameters.Add(p);
        var t = cmd.CreateParameter(); t.ParameterName = "@t"; t.Value = json; cmd.Parameters.Add(t);
        TransientRetry.Run(() => cmd.ExecuteNonQuery());
        cmd.Parameters.Clear();
    }

    [Test]
    public void IndexOnly_RegularIndex_HonorsShouldApplyExpression()
    {
        const string tableName = "IxGate_Regular";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}; CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Title VARCHAR(200) NULL);";
        cmd.ExecuteNonQuery();

        var json = $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[{{tableName}}]",
                "Indexes": [
                    { "Name": "[IX_Skip]", "IndexColumns": "[Id]", "ShouldApplyExpression": "1 = 0" },
                    { "Name": "[IX_Keep]", "IndexColumns": "[Title]", "ShouldApplyExpression": "1 = 1" }
                ]
            }]
            """;
        RunIndexOnlyQuenchJson(cmd, json);

        Assert.Multiple(() =>
        {
            Assert.That(IndexExists(cmd, tableName, "IX_Skip"), Is.False, "gated-out index (1=0) must not be created");
            Assert.That(IndexExists(cmd, tableName, "IX_Keep"), Is.True, "gated-in index (1=1) must be created");
        });

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void IndexOnly_XmlIndex_HonorsShouldApplyExpression()
    {
        const string tableName = "IxGate_Xml";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        cmd.CommandText = $@"
DROP TABLE IF EXISTS dbo.{tableName};
CREATE TABLE dbo.{tableName} (Id INT NOT NULL CONSTRAINT PK_{tableName} PRIMARY KEY CLUSTERED, Data XML NULL);";
        cmd.ExecuteNonQuery();

        var json = $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[{{tableName}}]",
                "XmlIndexes": [
                    { "Name": "[XI_Skip]", "Column": "[Data]", "IsPrimary": true, "ShouldApplyExpression": "1 = 0" }
                ]
            }]
            """;
        RunIndexOnlyQuenchJson(cmd, json);

        Assert.That(IndexExists(cmd, tableName, "XI_Skip"), Is.False, "gated-out XML index (1=0) must not be created");

        var jsonKeep = $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[{{tableName}}]",
                "XmlIndexes": [
                    { "Name": "[XI_Keep]", "Column": "[Data]", "IsPrimary": true, "ShouldApplyExpression": "1 = 1" }
                ]
            }]
            """;
        RunIndexOnlyQuenchJson(cmd, jsonKeep);
        Assert.That(IndexExists(cmd, tableName, "XI_Keep"), Is.True, "gated-in XML index (1=1) must be created");

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void IndexOnly_Statistic_HonorsShouldApplyExpression()
    {
        const string tableName = "IxGate_Stat";
        using var conn = DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}; CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Title VARCHAR(200) NULL);";
        cmd.ExecuteNonQuery();

        var json = $$"""
            [{
                "Schema": "[dbo]",
                "Name": "[{{tableName}}]",
                "Statistics": [
                    { "Name": "[ST_Skip]", "Columns": "[Id]", "ShouldApplyExpression": "1 = 0" },
                    { "Name": "[ST_Keep]", "Columns": "[Title]", "ShouldApplyExpression": "1 = 1" }
                ]
            }]
            """;
        RunIndexOnlyQuenchJson(cmd, json);

        Assert.Multiple(() =>
        {
            Assert.That(StatisticExists(cmd, tableName, "ST_Skip"), Is.False, "gated-out statistic (1=0) must not be created");
            Assert.That(StatisticExists(cmd, tableName, "ST_Keep"), Is.True, "gated-in statistic (1=1) must be created");
        });

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}";
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    [Test]
    public void IndexOnly_CreatingIndex_EchoesVariantNameInOperationMessage()
    {
        const string tableName = "IxVariant_Log";
        var messages = new System.Collections.Generic.List<string>();
        using var conn = (SqlConnection)DbConnectionFactory.ForPlatform(Platform.SqlServer).GetDbConnection(_connectionString);
        conn.InfoMessage += (_, e) => { foreach (SqlError err in e.Errors) messages.Add(err.Message); };
        conn.Open();
        conn.ChangeDatabase(_mainDb);
        using var cmd = (DbCommand)conn.CreateCommand();

        cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}; CREATE TABLE dbo.{tableName} (Id INT NOT NULL, Title VARCHAR(200) NULL);";
        cmd.ExecuteNonQuery();
        try
        {
            var json = $$"""
                [{
                    "Schema": "[dbo]",
                    "Name": "[{{tableName}}]",
                    "Indexes": [ { "Name": "[IX_{{tableName}}]", "IndexColumns": "[Id]", "VariantName": "Modern engines" } ]
                }]
                """;
            cmd.CommandTimeout = 300;
            cmd.CommandText = "EXEC SchemaSmith.IndexOnlyQuench @ProductName = @p, @TableDefinitions = @t, @WhatIf = 0";
            cmd.Parameters.Clear();
            var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = _productName; cmd.Parameters.Add(p);
            var t = cmd.CreateParameter(); t.ParameterName = "@t"; t.Value = json; cmd.Parameters.Add(t);
            TransientRetry.Run(() => cmd.ExecuteNonQuery());
            cmd.Parameters.Clear();

            Assert.That(messages, Has.Some.Contains("(variant: Modern engines)"));
        }
        finally
        {
            cmd.CommandText = $"DROP TABLE IF EXISTS dbo.{tableName}";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static string TwoVariantJson(string tableName, string matchDbName, string nonMatchDbName) =>
        GatedTwoVariantJson(tableName, $"DB_NAME() = '{matchDbName}'", $"DB_NAME() = '{nonMatchDbName}'");

    private static string GatedTwoVariantJson(string tableName, string expressionA, string expressionB) => $$"""
        [
          { "FullTextCatalog": "[FT_Catalog]", "KeyIndex": "[UDX_{{tableName}}]", "Columns": "[Title]", "ChangeTracking": "OFF", "ShouldApplyExpression": "{{expressionA}}" },
          { "FullTextCatalog": "[FT_Catalog2]", "KeyIndex": "[UDX_{{tableName}}]", "Columns": "[Title]", "ChangeTracking": "OFF", "ShouldApplyExpression": "{{expressionB}}" }
        ]
        """;
}
