// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Utility;
using Newtonsoft.Json;

namespace SchemaTongs.IntegrationTests.PostgreSQL;

[Category("PostgreSQL")]
public class GenerateMaterializedViewJsonTests
{
    private string _integrationDb = "";
    private string _connectionString;
    private string _testConnectionString;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var connProps = ConnectionString.ReadProperties(config, "PostgreSQL:ConnectionProperties");
        _connectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], "postgres", config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
        _integrationDb = GenerateUniqueDBName("GenMatViewJson");

        CreateTestDatabases();

        _testConnectionString = ConnectionString.Build(Platform.PostgreSQL, config["PostgreSQL:Server"], _integrationDb, config["PostgreSQL:User"], config["PostgreSQL:Password"], config["PostgreSQL:Port"], connProps);
    }

    [Test]
    public void ShouldGenerateCorrectJsonForMaterializedView()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
CREATE TABLE public.test_orders (customer_id INT, total DECIMAL(10,2));
INSERT INTO public.test_orders VALUES (1, 100.00), (1, 200.00), (2, 50.00);

CREATE MATERIALIZED VIEW public.mv_order_summary AS
SELECT customer_id, COUNT(*) AS order_count, SUM(total) AS total_spent
FROM public.test_orders
GROUP BY customer_id
WITH DATA;

CREATE UNIQUE INDEX ix_mv_order_summary_customer ON public.mv_order_summary (customer_id);
COMMENT ON INDEX public.ix_mv_order_summary_customer IS 'by customer';
";
        cmd.ExecuteNonQuery();

        // Install the extraction function
        cmd.CommandText = ResourceLoader.Load("SchemaSmith.GenerateMaterializedViewJson.sql", Platform.PostgreSQL);
        cmd.ExecuteNonQuery();

        // Call it
        cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateMaterializedViewJson\"('public', 'mv_order_summary')";
        var json = cmd.ExecuteScalar()?.ToString();

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        var view = JsonConvert.DeserializeObject<PostgreSqlMaterializedView>(json);
        Assert.That(view, Is.Not.Null);
        Assert.That(view.Schema, Is.EqualTo("public"));
        Assert.That(view.Name, Is.EqualTo("mv_order_summary"));
        Assert.That(view.Definition, Does.Contain("customer_id"));
        Assert.That(view.WithData, Is.True);
        Assert.That(view.AccessMethod, Is.EqualTo("heap"));
        Assert.That(view.Indexes, Has.Count.EqualTo(1));
        Assert.That(view.Indexes[0].Name, Is.EqualTo("ix_mv_order_summary_customer"));
        Assert.That(view.Indexes[0].Unique, Is.True);
        Assert.That(view.Indexes[0].IndexColumns, Is.EqualTo("customer_id"));
        Assert.That(view.Indexes[0].Description, Is.EqualTo("by customer"), "an index's comment is its Description");

        conn.Close();
    }

    [Test]
    public void ShouldHandleMaterializedViewWithNoIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
CREATE TABLE public.test_products (id INT, name VARCHAR(100), price DECIMAL(10,2));
INSERT INTO public.test_products VALUES (1, 'Widget', 9.99), (2, 'Gadget', 19.99);

CREATE MATERIALIZED VIEW public.mv_product_summary AS
SELECT COUNT(*) AS product_count, SUM(price) AS total_price
FROM public.test_products
WITH DATA;
";
        cmd.ExecuteNonQuery();

        // Install the extraction function
        cmd.CommandText = ResourceLoader.Load("SchemaSmith.GenerateMaterializedViewJson.sql", Platform.PostgreSQL);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateMaterializedViewJson\"('public', 'mv_product_summary')";
        var json = cmd.ExecuteScalar()?.ToString();

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        var view = JsonConvert.DeserializeObject<PostgreSqlMaterializedView>(json);
        Assert.That(view, Is.Not.Null);
        Assert.That(view.Schema, Is.EqualTo("public"));
        Assert.That(view.Name, Is.EqualTo("mv_product_summary"));
        Assert.That(view.Definition, Does.Contain("product_count"));
        Assert.That(view.WithData, Is.True);
        Assert.That(view.Indexes, Is.Null.Or.Empty);

        conn.Close();
    }

    [Test]
    public void ShouldHandleMaterializedViewWithNoData()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
CREATE TABLE public.test_employees (id INT, department VARCHAR(50), salary DECIMAL(10,2));

CREATE MATERIALIZED VIEW public.mv_dept_summary AS
SELECT department, COUNT(*) AS emp_count, AVG(salary) AS avg_salary
FROM public.test_employees
GROUP BY department
WITH NO DATA;
";
        cmd.ExecuteNonQuery();

        // Install the extraction function
        cmd.CommandText = ResourceLoader.Load("SchemaSmith.GenerateMaterializedViewJson.sql", Platform.PostgreSQL);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateMaterializedViewJson\"('public', 'mv_dept_summary')";
        var json = cmd.ExecuteScalar()?.ToString();

        Assert.That(json, Is.Not.Null.And.Not.Empty);
        var view = JsonConvert.DeserializeObject<PostgreSqlMaterializedView>(json);
        Assert.That(view, Is.Not.Null);
        Assert.That(view.Schema, Is.EqualTo("public"));
        Assert.That(view.Name, Is.EqualTo("mv_dept_summary"));
        Assert.That(view.WithData, Is.False);

        conn.Close();
    }

    [OneTimeTearDown]
    public void RunAfterAnyTests()
    {
        DropTestDatabases();
    }

    private void CreateTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @$"
CREATE DATABASE ""{_integrationDb}"";
";
        cmd.ExecuteNonQuery();

        conn.ChangeDatabase(_integrationDb);
        ForgeKindler.KindleTheForge(cmd, Platform.PostgreSQL);

        conn.Close();
    }

    [Test]
    public void ShouldExtractIndexStorageParameters_FillFactorOnlyWhenSet_AndKeyOperatorClass()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_testConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE public.test_extras (id INT, name TEXT);
CREATE MATERIALIZED VIEW public.mv_extras AS SELECT id, name FROM public.test_extras WITH DATA;
CREATE INDEX ix_mv_extras_brin ON public.mv_extras USING brin (id) WITH (pages_per_range = 16);
CREATE INDEX ix_mv_extras_name ON public.mv_extras (name COLLATE ""C"" text_pattern_ops) WITH (fillfactor = 70);
CREATE INDEX ix_mv_extras_plain ON public.mv_extras (id);
";
        cmd.ExecuteNonQuery();
        cmd.CommandText = ResourceLoader.Load("SchemaSmith.GenerateMaterializedViewJson.sql", Platform.PostgreSQL);
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT \"SchemaSmith\".\"GenerateMaterializedViewJson\"('public', 'mv_extras')";
        var view = JsonConvert.DeserializeObject<PostgreSqlMaterializedView>(cmd.ExecuteScalar()?.ToString());
        PostgreSqlIndex Index(string name) => view.Indexes.Single(i => i.Name == name);
        Assert.Multiple(() =>
        {
            Assert.That(Index("ix_mv_extras_brin").StorageParameters, Is.EqualTo(new Dictionary<string, string> { ["pages_per_range"] = "16" }));
            Assert.That(Index("ix_mv_extras_brin").FillFactor, Is.Zero, "a brin index has no fill factor");
            Assert.That(Index("ix_mv_extras_name").IndexColumns, Is.EqualTo("name COLLATE \"C\" text_pattern_ops"));
            Assert.That(Index("ix_mv_extras_name").FillFactor, Is.EqualTo(70));
            Assert.That(Index("ix_mv_extras_name").StorageParameters, Is.Null, "fillfactor belongs to FillFactor, not the map");
            Assert.That(Index("ix_mv_extras_plain").FillFactor, Is.Zero, "no option, no declaration");
            Assert.That(Index("ix_mv_extras_plain").Tablespace, Is.Null);
        });
        conn.Close();
    }

    private static string GenerateUniqueDBName(string dbName)
    {
        dbName = dbName ?? throw new ArgumentNullException(nameof(dbName));
        var uniqueSegment = Guid.NewGuid().ToString().Replace(" - ", "_").Substring(0, 8);
        return $"{dbName}_Test_{DateTime.Now:yyyyMMdd_HHmmss}_{uniqueSegment}";
    }

    private void DropTestDatabases()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform.PostgreSQL).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        DropOneDatabase(cmd, _integrationDb);

        conn.Close();
    }

    private static void DropOneDatabase(IDbCommand cmd, string dbName)
    {
        cmd.CommandText = @$"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}' AND pid <> pg_backend_pid();";
        cmd.ExecuteNonQuery();
        cmd.CommandText = @$"DROP DATABASE IF EXISTS ""{dbName}"";";
        cmd.ExecuteNonQuery();
    }
}
