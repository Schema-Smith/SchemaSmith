// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using System;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for quenching a large schema (16+ tables) where all tables already exist.
/// Validates the INFORMATION_SCHEMA snapshot fix in ParseTableJson - the MySQL optimizer can
/// cache/materialize INFORMATION_SCHEMA results incorrectly in correlated NOT EXISTS subqueries,
/// causing tables to be incorrectly marked as "new" and triggering "Table already exists" errors.
/// </summary>
[Category("Integration")]
public abstract class TableQuench_LargeSchemaSharedTests : BaseTableQuenchTests
{
    private const string TestSchema = "LargeSchemaTests";

    [OneTimeSetUp]
    public void Setup()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        // Drop and recreate to ensure clean state
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{TestSchema}`";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"CREATE DATABASE `{TestSchema}`";
        cmd.ExecuteNonQuery();

        // Create 16 tables matching a Sakila-like structure (without FK constraints in DDL,
        // as the TestUser may lack REFERENCES privilege on ad-hoc databases).
        // FK constraints will be created by the quench procedures which run with SQL SECURITY DEFINER.
        // These must all exist BEFORE we run ParseTableJson so we can verify
        // that the optimizer fix correctly detects them as existing (NewTable=0).
        CreateSakilaTables(cmd);

        // Verify we actually created 16 tables
        cmd.CommandText = $@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{TestSchema}' AND TABLE_TYPE = 'BASE TABLE'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(16), "Setup should create 16 tables");
        cmd.ExecuteNonQuery();

        // Build JSON table definitions matching ALL 16 tables above.
        // The critical test: ParseTableJson must correctly identify ALL of these as existing (NewTable=0).
        // Without the INFORMATION_SCHEMA snapshot fix, the MySQL optimizer may incorrectly cache
        // INFORMATION_SCHEMA results in correlated NOT EXISTS subqueries, causing some tables
        // to be marked as NewTable=1, which leads to "Table already exists" errors.
        var json = """
            [
            {
                "Name": "language",
                "Columns": [
                    { "Name": "language_id", "DataType": "TINYINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "name", "DataType": "CHAR(20)", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "language_id" }
                ]
            },
            {
                "Name": "actor",
                "Columns": [
                    { "Name": "actor_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "first_name", "DataType": "VARCHAR(45)", "Nullable": false },
                    { "Name": "last_name", "DataType": "VARCHAR(45)", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "actor_id" },
                    { "Name": "idx_actor_last_name", "IndexColumns": "last_name" }
                ]
            },
            {
                "Name": "country",
                "Columns": [
                    { "Name": "country_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "country", "DataType": "VARCHAR(50)", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "country_id" }
                ]
            },
            {
                "Name": "city",
                "Columns": [
                    { "Name": "city_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "city", "DataType": "VARCHAR(50)", "Nullable": false },
                    { "Name": "country_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "city_id" },
                    { "Name": "idx_fk_country_id", "IndexColumns": "country_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_city_country", "Columns": "country_id", "RelatedTable": "country", "RelatedColumns": "country_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "film",
                "Columns": [
                    { "Name": "film_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "title", "DataType": "VARCHAR(128)", "Nullable": false },
                    { "Name": "description", "DataType": "TEXT", "Nullable": true },
                    { "Name": "release_year", "DataType": "YEAR", "Nullable": true },
                    { "Name": "language_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                    { "Name": "original_language_id", "DataType": "TINYINT UNSIGNED", "Nullable": true },
                    { "Name": "rental_duration", "DataType": "TINYINT UNSIGNED", "Nullable": false, "Default": "3" },
                    { "Name": "rental_rate", "DataType": "DECIMAL(4,2)", "Nullable": false, "Default": "4.99" },
                    { "Name": "length", "DataType": "SMALLINT UNSIGNED", "Nullable": true },
                    { "Name": "replacement_cost", "DataType": "DECIMAL(5,2)", "Nullable": false, "Default": "19.99" },
                    { "Name": "rating", "DataType": "ENUM('G','PG','PG-13','R','NC-17')", "Nullable": true, "Default": "'G'" },
                    { "Name": "special_features", "DataType": "SET('Trailers','Commentaries','Deleted Scenes','Behind the Scenes')", "Nullable": true },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "film_id" },
                    { "Name": "idx_title", "IndexColumns": "title" },
                    { "Name": "idx_fk_language_id", "IndexColumns": "language_id" },
                    { "Name": "idx_fk_original_language_id", "IndexColumns": "original_language_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_film_language", "Columns": "language_id", "RelatedTable": "language", "RelatedColumns": "language_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_film_language_original", "Columns": "original_language_id", "RelatedTable": "language", "RelatedColumns": "language_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "film_text",
                "Columns": [
                    { "Name": "film_id", "DataType": "SMALLINT", "Nullable": false },
                    { "Name": "title", "DataType": "VARCHAR(255)", "Nullable": false },
                    { "Name": "description", "DataType": "TEXT", "Nullable": true }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "film_id" }
                ]
            },
            {
                "Name": "category",
                "Columns": [
                    { "Name": "category_id", "DataType": "TINYINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "name", "DataType": "VARCHAR(25)", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "category_id" }
                ]
            },
            {
                "Name": "film_category",
                "Columns": [
                    { "Name": "film_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "category_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "film_id, category_id" },
                    { "Name": "fk_film_category_category", "IndexColumns": "category_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_film_category_film", "Columns": "film_id", "RelatedTable": "film", "RelatedColumns": "film_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_film_category_category", "Columns": "category_id", "RelatedTable": "category", "RelatedColumns": "category_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "film_actor",
                "Columns": [
                    { "Name": "actor_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "film_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "actor_id, film_id" },
                    { "Name": "idx_fk_film_id", "IndexColumns": "film_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_film_actor_actor", "Columns": "actor_id", "RelatedTable": "actor", "RelatedColumns": "actor_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_film_actor_film", "Columns": "film_id", "RelatedTable": "film", "RelatedColumns": "film_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "address",
                "Columns": [
                    { "Name": "address_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "address", "DataType": "VARCHAR(50)", "Nullable": false },
                    { "Name": "address2", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "district", "DataType": "VARCHAR(20)", "Nullable": false },
                    { "Name": "city_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "postal_code", "DataType": "VARCHAR(10)", "Nullable": true },
                    { "Name": "phone", "DataType": "VARCHAR(20)", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "address_id" },
                    { "Name": "idx_fk_city_id", "IndexColumns": "city_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_address_city", "Columns": "city_id", "RelatedTable": "city", "RelatedColumns": "city_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "staff",
                "Columns": [
                    { "Name": "staff_id", "DataType": "TINYINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "first_name", "DataType": "VARCHAR(45)", "Nullable": false },
                    { "Name": "last_name", "DataType": "VARCHAR(45)", "Nullable": false },
                    { "Name": "address_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "email", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "active", "DataType": "TINYINT(1)", "Nullable": false, "Default": "1" },
                    { "Name": "username", "DataType": "VARCHAR(16)", "Nullable": false },
                    { "Name": "password", "DataType": "VARCHAR(40)", "Nullable": true },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "staff_id" },
                    { "Name": "idx_fk_address_id", "IndexColumns": "address_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_staff_address", "Columns": "address_id", "RelatedTable": "address", "RelatedColumns": "address_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "store",
                "Columns": [
                    { "Name": "store_id", "DataType": "TINYINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "manager_staff_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                    { "Name": "address_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "store_id" },
                    { "Name": "idx_unique_manager", "IndexColumns": "manager_staff_id", "Unique": true },
                    { "Name": "idx_fk_address_id", "IndexColumns": "address_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_store_staff", "Columns": "manager_staff_id", "RelatedTable": "staff", "RelatedColumns": "staff_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_store_address", "Columns": "address_id", "RelatedTable": "address", "RelatedColumns": "address_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "customer",
                "Columns": [
                    { "Name": "customer_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "store_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                    { "Name": "first_name", "DataType": "VARCHAR(45)", "Nullable": false },
                    { "Name": "last_name", "DataType": "VARCHAR(45)", "Nullable": false },
                    { "Name": "email", "DataType": "VARCHAR(50)", "Nullable": true },
                    { "Name": "address_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "active", "DataType": "TINYINT(1)", "Nullable": false, "Default": "1" },
                    { "Name": "create_date", "DataType": "DATETIME", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": true, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "customer_id" },
                    { "Name": "idx_fk_store_id", "IndexColumns": "store_id" },
                    { "Name": "idx_fk_address_id", "IndexColumns": "address_id" },
                    { "Name": "idx_last_name", "IndexColumns": "last_name" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_customer_address", "Columns": "address_id", "RelatedTable": "address", "RelatedColumns": "address_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_customer_store", "Columns": "store_id", "RelatedTable": "store", "RelatedColumns": "store_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "inventory",
                "Columns": [
                    { "Name": "inventory_id", "DataType": "MEDIUMINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "film_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "store_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "inventory_id" },
                    { "Name": "idx_fk_film_id", "IndexColumns": "film_id" },
                    { "Name": "idx_store_id_film_id", "IndexColumns": "store_id, film_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_inventory_film", "Columns": "film_id", "RelatedTable": "film", "RelatedColumns": "film_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_inventory_store", "Columns": "store_id", "RelatedTable": "store", "RelatedColumns": "store_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "rental",
                "Columns": [
                    { "Name": "rental_id", "DataType": "INT", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "rental_date", "DataType": "DATETIME", "Nullable": false },
                    { "Name": "inventory_id", "DataType": "MEDIUMINT UNSIGNED", "Nullable": false },
                    { "Name": "customer_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "return_date", "DataType": "DATETIME", "Nullable": true },
                    { "Name": "staff_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "rental_id" },
                    { "Name": "idx_rental_uq", "IndexColumns": "rental_date, inventory_id, customer_id", "Unique": true },
                    { "Name": "idx_fk_inventory_id", "IndexColumns": "inventory_id" },
                    { "Name": "idx_fk_customer_id", "IndexColumns": "customer_id" },
                    { "Name": "idx_fk_staff_id", "IndexColumns": "staff_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_rental_inventory", "Columns": "inventory_id", "RelatedTable": "inventory", "RelatedColumns": "inventory_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_rental_customer", "Columns": "customer_id", "RelatedTable": "customer", "RelatedColumns": "customer_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_rental_staff", "Columns": "staff_id", "RelatedTable": "staff", "RelatedColumns": "staff_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
                ]
            },
            {
                "Name": "payment",
                "Columns": [
                    { "Name": "payment_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                    { "Name": "customer_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                    { "Name": "staff_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                    { "Name": "rental_id", "DataType": "INT", "Nullable": true },
                    { "Name": "amount", "DataType": "DECIMAL(5,2)", "Nullable": false },
                    { "Name": "payment_date", "DataType": "DATETIME", "Nullable": false },
                    { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": true, "Default": "CURRENT_TIMESTAMP" }
                ],
                "Indexes": [
                    { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "payment_id" },
                    { "Name": "idx_fk_staff_id", "IndexColumns": "staff_id" },
                    { "Name": "idx_fk_customer_id", "IndexColumns": "customer_id" },
                    { "Name": "fk_payment_rental", "IndexColumns": "rental_id" }
                ],
                "ForeignKeys": [
                    { "Name": "fk_payment_customer", "Columns": "customer_id", "RelatedTable": "customer", "RelatedColumns": "customer_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_payment_staff", "Columns": "staff_id", "RelatedTable": "staff", "RelatedColumns": "staff_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                    { "Name": "fk_payment_rental", "Columns": "rental_id", "RelatedTable": "rental", "RelatedColumns": "rental_id", "DeleteAction": "SET NULL", "UpdateAction": "CASCADE" }
                ]
            }
            ]
            """;

        // Run the full quench pipeline - this is the critical test.
        // Without the INFORMATION_SCHEMA snapshot fix, this will fail with "Table already exists"
        // because ParseTableJson incorrectly marks existing tables as NewTable=1.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{json.Replace("'", "''")}')";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ForeignKeyQuench('{_productName}', '{TestSchema}', 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        conn.Close();
    }

    [Test]
    public void TableQuench_AllExistingTablesShouldRemainAfterQuench()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Verify all 16 tables still exist
        var expectedTables = new[]
        {
            "language", "actor", "country", "city", "film", "film_text",
            "category", "film_category", "film_actor", "address", "staff",
            "store", "customer", "inventory", "rental", "payment"
        };

        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_TYPE = 'BASE TABLE'";
        var tableCount = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.That(tableCount, Is.EqualTo(16), "All 16 tables should exist after quench");

        foreach (var table in expectedTables)
        {
            cmd.CommandText = $@"
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = '{TestSchema}'
                  AND TABLE_NAME = '{table}'
                  AND TABLE_TYPE = 'BASE TABLE'";
            Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                $"Table '{table}' should still exist after quench");
        }

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldPreserveAllForeignKeys()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Verify key FKs are intact (proves tables weren't dropped and recreated)
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND CONSTRAINT_TYPE = 'FOREIGN KEY'";
        var fkCount = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.That(fkCount, Is.GreaterThanOrEqualTo(16),
            "At least 16 foreign keys should exist across the schema");

        conn.Close();
    }

    [Test]
    public void TableQuench_ShouldPreserveAllIndexes()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Verify indexes are preserved
        cmd.CommandText = $@"
            SELECT COUNT(DISTINCT INDEX_NAME, TABLE_NAME) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = '{TestSchema}'";
        var indexCount = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.That(indexCount, Is.GreaterThanOrEqualTo(30),
            "At least 30 indexes should exist across the schema");

        conn.Close();
    }

    [Test]
    public void TableQuench_RerunShouldBeIdempotent()
    {
        // Run the entire quench pipeline a second time - should be a no-op
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var json = BuildSakilaJson();

        // This second run should succeed without errors (idempotent)
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{json.Replace("'", "''")}')";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 0)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 0, 0, 1, 1, 1, 1, 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Verify tables still intact after second run
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{TestSchema}'
              AND TABLE_TYPE = 'BASE TABLE'";
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(16));

        conn.Close();
    }

    [Test]
    public void TableQuench_WhatIfModeShouldNotError()
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;

        var json = BuildSakilaJson();

        // WhatIf mode (p_WhatIf = 1) should produce no errors
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ParseTableJson('{TestSchema}', '{json.Replace("'", "''")}')";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingTableAndColumnQuench('{TestSchema}', 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        // Trailing 0, 1 are DropUnknownIndexes and DropIndexesRemovedFromProduct: index removal now
        // happens here, and the 1 carries over from the MissingIndexesAndConstraintsQuench call below.
        // WhatIf=1 keeps both procedures read-only regardless.
        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_ModifiedTableQuench('{_productName}', '{TestSchema}', 1, 0, 1, 1, 1, 1, 0, 0, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        cmd.CommandText = $"CALL `{_mainDb}`.SchemaSmith_MissingIndexesAndConstraintsQuench('{_productName}', '{TestSchema}', 1, 1)";
        TransientRetry.Run(() => cmd.ExecuteNonQuery());

        conn.Close();
    }

    private void CreateSakilaTables(IDbCommand cmd)
    {
        // Create all 16 tables without FK constraints (TestUser may lack REFERENCES privilege).
        // FKs will be added by the quench procedures which run with SQL SECURITY DEFINER.
        // Each table is created individually to avoid multi-statement issues.
        var tables = new[]
        {
            $@"CREATE TABLE `{TestSchema}`.`language` (
                language_id TINYINT UNSIGNED NOT NULL AUTO_INCREMENT,
                name CHAR(20) NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (language_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`actor` (
                actor_id SMALLINT UNSIGNED NOT NULL AUTO_INCREMENT,
                first_name VARCHAR(45) NOT NULL,
                last_name VARCHAR(45) NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (actor_id),
                KEY idx_actor_last_name (last_name)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`country` (
                country_id SMALLINT UNSIGNED NOT NULL AUTO_INCREMENT,
                country VARCHAR(50) NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (country_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`city` (
                city_id SMALLINT UNSIGNED NOT NULL AUTO_INCREMENT,
                city VARCHAR(50) NOT NULL,
                country_id SMALLINT UNSIGNED NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (city_id),
                KEY idx_fk_country_id (country_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`film` (
                film_id SMALLINT UNSIGNED NOT NULL AUTO_INCREMENT,
                title VARCHAR(128) NOT NULL,
                description TEXT,
                release_year YEAR,
                language_id TINYINT UNSIGNED NOT NULL,
                original_language_id TINYINT UNSIGNED DEFAULT NULL,
                rental_duration TINYINT UNSIGNED NOT NULL DEFAULT 3,
                rental_rate DECIMAL(4,2) NOT NULL DEFAULT 4.99,
                length SMALLINT UNSIGNED DEFAULT NULL,
                replacement_cost DECIMAL(5,2) NOT NULL DEFAULT 19.99,
                rating ENUM('G','PG','PG-13','R','NC-17') DEFAULT 'G',
                special_features SET('Trailers','Commentaries','Deleted Scenes','Behind the Scenes') DEFAULT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (film_id),
                KEY idx_title (title),
                KEY idx_fk_language_id (language_id),
                KEY idx_fk_original_language_id (original_language_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`film_text` (
                film_id SMALLINT NOT NULL,
                title VARCHAR(255) NOT NULL,
                description TEXT,
                PRIMARY KEY (film_id),
                FULLTEXT KEY idx_title_description (title, description)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`category` (
                category_id TINYINT UNSIGNED NOT NULL AUTO_INCREMENT,
                name VARCHAR(25) NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (category_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`film_category` (
                film_id SMALLINT UNSIGNED NOT NULL,
                category_id TINYINT UNSIGNED NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (film_id, category_id),
                KEY fk_film_category_category (category_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`film_actor` (
                actor_id SMALLINT UNSIGNED NOT NULL,
                film_id SMALLINT UNSIGNED NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (actor_id, film_id),
                KEY idx_fk_film_id (film_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`address` (
                address_id SMALLINT UNSIGNED NOT NULL AUTO_INCREMENT,
                address VARCHAR(50) NOT NULL,
                address2 VARCHAR(50) DEFAULT NULL,
                district VARCHAR(20) NOT NULL,
                city_id SMALLINT UNSIGNED NOT NULL,
                postal_code VARCHAR(10) DEFAULT NULL,
                phone VARCHAR(20) NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (address_id),
                KEY idx_fk_city_id (city_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`staff` (
                staff_id TINYINT UNSIGNED NOT NULL AUTO_INCREMENT,
                first_name VARCHAR(45) NOT NULL,
                last_name VARCHAR(45) NOT NULL,
                address_id SMALLINT UNSIGNED NOT NULL,
                email VARCHAR(50) DEFAULT NULL,
                active TINYINT(1) NOT NULL DEFAULT 1,
                username VARCHAR(16) NOT NULL,
                password VARCHAR(40) DEFAULT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (staff_id),
                KEY idx_fk_address_id (address_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`store` (
                store_id TINYINT UNSIGNED NOT NULL AUTO_INCREMENT,
                manager_staff_id TINYINT UNSIGNED NOT NULL,
                address_id SMALLINT UNSIGNED NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (store_id),
                UNIQUE KEY idx_unique_manager (manager_staff_id),
                KEY idx_fk_address_id (address_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`customer` (
                customer_id SMALLINT UNSIGNED NOT NULL AUTO_INCREMENT,
                store_id TINYINT UNSIGNED NOT NULL,
                first_name VARCHAR(45) NOT NULL,
                last_name VARCHAR(45) NOT NULL,
                email VARCHAR(50) DEFAULT NULL,
                address_id SMALLINT UNSIGNED NOT NULL,
                active TINYINT(1) NOT NULL DEFAULT 1,
                create_date DATETIME NOT NULL,
                last_update TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (customer_id),
                KEY idx_fk_store_id (store_id),
                KEY idx_fk_address_id (address_id),
                KEY idx_last_name (last_name)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`inventory` (
                inventory_id MEDIUMINT UNSIGNED NOT NULL AUTO_INCREMENT,
                film_id SMALLINT UNSIGNED NOT NULL,
                store_id TINYINT UNSIGNED NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (inventory_id),
                KEY idx_fk_film_id (film_id),
                KEY idx_store_id_film_id (store_id, film_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`rental` (
                rental_id INT NOT NULL AUTO_INCREMENT,
                rental_date DATETIME NOT NULL,
                inventory_id MEDIUMINT UNSIGNED NOT NULL,
                customer_id SMALLINT UNSIGNED NOT NULL,
                return_date DATETIME DEFAULT NULL,
                staff_id TINYINT UNSIGNED NOT NULL,
                last_update TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (rental_id),
                UNIQUE KEY idx_rental_uq (rental_date, inventory_id, customer_id),
                KEY idx_fk_inventory_id (inventory_id),
                KEY idx_fk_customer_id (customer_id),
                KEY idx_fk_staff_id (staff_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4",

            $@"CREATE TABLE `{TestSchema}`.`payment` (
                payment_id SMALLINT UNSIGNED NOT NULL AUTO_INCREMENT,
                customer_id SMALLINT UNSIGNED NOT NULL,
                staff_id TINYINT UNSIGNED NOT NULL,
                rental_id INT DEFAULT NULL,
                amount DECIMAL(5,2) NOT NULL,
                payment_date DATETIME NOT NULL,
                last_update TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (payment_id),
                KEY idx_fk_staff_id (staff_id),
                KEY idx_fk_customer_id (customer_id),
                KEY fk_payment_rental (rental_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4"
        };

        foreach (var ddl in tables)
        {
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }
    }

    private static string BuildSakilaJson()
    {
        return """
            [
            { "Name": "language", "Columns": [
                { "Name": "language_id", "DataType": "TINYINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "name", "DataType": "CHAR(20)", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [{ "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "language_id" }] },
            { "Name": "actor", "Columns": [
                { "Name": "actor_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "first_name", "DataType": "VARCHAR(45)", "Nullable": false },
                { "Name": "last_name", "DataType": "VARCHAR(45)", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "actor_id" },
                { "Name": "idx_actor_last_name", "IndexColumns": "last_name" }
            ] },
            { "Name": "country", "Columns": [
                { "Name": "country_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "country", "DataType": "VARCHAR(50)", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [{ "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "country_id" }] },
            { "Name": "city", "Columns": [
                { "Name": "city_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "city", "DataType": "VARCHAR(50)", "Nullable": false },
                { "Name": "country_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "city_id" },
                { "Name": "idx_fk_country_id", "IndexColumns": "country_id" }
            ], "ForeignKeys": [
                { "Name": "fk_city_country", "Columns": "country_id", "RelatedTable": "country", "RelatedColumns": "country_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "film", "Columns": [
                { "Name": "film_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "title", "DataType": "VARCHAR(128)", "Nullable": false },
                { "Name": "description", "DataType": "TEXT", "Nullable": true },
                { "Name": "release_year", "DataType": "YEAR", "Nullable": true },
                { "Name": "language_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                { "Name": "original_language_id", "DataType": "TINYINT UNSIGNED", "Nullable": true },
                { "Name": "rental_duration", "DataType": "TINYINT UNSIGNED", "Nullable": false, "Default": "3" },
                { "Name": "rental_rate", "DataType": "DECIMAL(4,2)", "Nullable": false, "Default": "4.99" },
                { "Name": "length", "DataType": "SMALLINT UNSIGNED", "Nullable": true },
                { "Name": "replacement_cost", "DataType": "DECIMAL(5,2)", "Nullable": false, "Default": "19.99" },
                { "Name": "rating", "DataType": "ENUM('G','PG','PG-13','R','NC-17')", "Nullable": true, "Default": "'G'" },
                { "Name": "special_features", "DataType": "SET('Trailers','Commentaries','Deleted Scenes','Behind the Scenes')", "Nullable": true },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "film_id" },
                { "Name": "idx_title", "IndexColumns": "title" },
                { "Name": "idx_fk_language_id", "IndexColumns": "language_id" },
                { "Name": "idx_fk_original_language_id", "IndexColumns": "original_language_id" }
            ], "ForeignKeys": [
                { "Name": "fk_film_language", "Columns": "language_id", "RelatedTable": "language", "RelatedColumns": "language_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_film_language_original", "Columns": "original_language_id", "RelatedTable": "language", "RelatedColumns": "language_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "film_text", "Columns": [
                { "Name": "film_id", "DataType": "SMALLINT", "Nullable": false },
                { "Name": "title", "DataType": "VARCHAR(255)", "Nullable": false },
                { "Name": "description", "DataType": "TEXT", "Nullable": true }
            ], "Indexes": [{ "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "film_id" }] },
            { "Name": "category", "Columns": [
                { "Name": "category_id", "DataType": "TINYINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "name", "DataType": "VARCHAR(25)", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [{ "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "category_id" }] },
            { "Name": "film_category", "Columns": [
                { "Name": "film_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "category_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "film_id, category_id" },
                { "Name": "fk_film_category_category", "IndexColumns": "category_id" }
            ], "ForeignKeys": [
                { "Name": "fk_film_category_film", "Columns": "film_id", "RelatedTable": "film", "RelatedColumns": "film_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_film_category_category", "Columns": "category_id", "RelatedTable": "category", "RelatedColumns": "category_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "film_actor", "Columns": [
                { "Name": "actor_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "film_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "actor_id, film_id" },
                { "Name": "idx_fk_film_id", "IndexColumns": "film_id" }
            ], "ForeignKeys": [
                { "Name": "fk_film_actor_actor", "Columns": "actor_id", "RelatedTable": "actor", "RelatedColumns": "actor_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_film_actor_film", "Columns": "film_id", "RelatedTable": "film", "RelatedColumns": "film_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "address", "Columns": [
                { "Name": "address_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "address", "DataType": "VARCHAR(50)", "Nullable": false },
                { "Name": "address2", "DataType": "VARCHAR(50)", "Nullable": true },
                { "Name": "district", "DataType": "VARCHAR(20)", "Nullable": false },
                { "Name": "city_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "postal_code", "DataType": "VARCHAR(10)", "Nullable": true },
                { "Name": "phone", "DataType": "VARCHAR(20)", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "address_id" },
                { "Name": "idx_fk_city_id", "IndexColumns": "city_id" }
            ], "ForeignKeys": [
                { "Name": "fk_address_city", "Columns": "city_id", "RelatedTable": "city", "RelatedColumns": "city_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "staff", "Columns": [
                { "Name": "staff_id", "DataType": "TINYINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "first_name", "DataType": "VARCHAR(45)", "Nullable": false },
                { "Name": "last_name", "DataType": "VARCHAR(45)", "Nullable": false },
                { "Name": "address_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "email", "DataType": "VARCHAR(50)", "Nullable": true },
                { "Name": "active", "DataType": "TINYINT(1)", "Nullable": false, "Default": "1" },
                { "Name": "username", "DataType": "VARCHAR(16)", "Nullable": false },
                { "Name": "password", "DataType": "VARCHAR(40)", "Nullable": true },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "staff_id" },
                { "Name": "idx_fk_address_id", "IndexColumns": "address_id" }
            ], "ForeignKeys": [
                { "Name": "fk_staff_address", "Columns": "address_id", "RelatedTable": "address", "RelatedColumns": "address_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "store", "Columns": [
                { "Name": "store_id", "DataType": "TINYINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "manager_staff_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                { "Name": "address_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "store_id" },
                { "Name": "idx_unique_manager", "IndexColumns": "manager_staff_id", "Unique": true },
                { "Name": "idx_fk_address_id", "IndexColumns": "address_id" }
            ], "ForeignKeys": [
                { "Name": "fk_store_staff", "Columns": "manager_staff_id", "RelatedTable": "staff", "RelatedColumns": "staff_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_store_address", "Columns": "address_id", "RelatedTable": "address", "RelatedColumns": "address_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "customer", "Columns": [
                { "Name": "customer_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "store_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                { "Name": "first_name", "DataType": "VARCHAR(45)", "Nullable": false },
                { "Name": "last_name", "DataType": "VARCHAR(45)", "Nullable": false },
                { "Name": "email", "DataType": "VARCHAR(50)", "Nullable": true },
                { "Name": "address_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "active", "DataType": "TINYINT(1)", "Nullable": false, "Default": "1" },
                { "Name": "create_date", "DataType": "DATETIME", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": true, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "customer_id" },
                { "Name": "idx_fk_store_id", "IndexColumns": "store_id" },
                { "Name": "idx_fk_address_id", "IndexColumns": "address_id" },
                { "Name": "idx_last_name", "IndexColumns": "last_name" }
            ], "ForeignKeys": [
                { "Name": "fk_customer_address", "Columns": "address_id", "RelatedTable": "address", "RelatedColumns": "address_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_customer_store", "Columns": "store_id", "RelatedTable": "store", "RelatedColumns": "store_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "inventory", "Columns": [
                { "Name": "inventory_id", "DataType": "MEDIUMINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "film_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "store_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "inventory_id" },
                { "Name": "idx_fk_film_id", "IndexColumns": "film_id" },
                { "Name": "idx_store_id_film_id", "IndexColumns": "store_id, film_id" }
            ], "ForeignKeys": [
                { "Name": "fk_inventory_film", "Columns": "film_id", "RelatedTable": "film", "RelatedColumns": "film_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_inventory_store", "Columns": "store_id", "RelatedTable": "store", "RelatedColumns": "store_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "rental", "Columns": [
                { "Name": "rental_id", "DataType": "INT", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "rental_date", "DataType": "DATETIME", "Nullable": false },
                { "Name": "inventory_id", "DataType": "MEDIUMINT UNSIGNED", "Nullable": false },
                { "Name": "customer_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "return_date", "DataType": "DATETIME", "Nullable": true },
                { "Name": "staff_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": false, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "rental_id" },
                { "Name": "idx_rental_uq", "IndexColumns": "rental_date, inventory_id, customer_id", "Unique": true },
                { "Name": "idx_fk_inventory_id", "IndexColumns": "inventory_id" },
                { "Name": "idx_fk_customer_id", "IndexColumns": "customer_id" },
                { "Name": "idx_fk_staff_id", "IndexColumns": "staff_id" }
            ], "ForeignKeys": [
                { "Name": "fk_rental_inventory", "Columns": "inventory_id", "RelatedTable": "inventory", "RelatedColumns": "inventory_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_rental_customer", "Columns": "customer_id", "RelatedTable": "customer", "RelatedColumns": "customer_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_rental_staff", "Columns": "staff_id", "RelatedTable": "staff", "RelatedColumns": "staff_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" }
            ] },
            { "Name": "payment", "Columns": [
                { "Name": "payment_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false, "AutoIncrement": 1 },
                { "Name": "customer_id", "DataType": "SMALLINT UNSIGNED", "Nullable": false },
                { "Name": "staff_id", "DataType": "TINYINT UNSIGNED", "Nullable": false },
                { "Name": "rental_id", "DataType": "INT", "Nullable": true },
                { "Name": "amount", "DataType": "DECIMAL(5,2)", "Nullable": false },
                { "Name": "payment_date", "DataType": "DATETIME", "Nullable": false },
                { "Name": "last_update", "DataType": "TIMESTAMP", "Nullable": true, "Default": "CURRENT_TIMESTAMP" }
            ], "Indexes": [
                { "Name": "PRIMARY", "PrimaryKey": true, "IndexColumns": "payment_id" },
                { "Name": "idx_fk_staff_id", "IndexColumns": "staff_id" },
                { "Name": "idx_fk_customer_id", "IndexColumns": "customer_id" },
                { "Name": "fk_payment_rental", "IndexColumns": "rental_id" }
            ], "ForeignKeys": [
                { "Name": "fk_payment_customer", "Columns": "customer_id", "RelatedTable": "customer", "RelatedColumns": "customer_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_payment_staff", "Columns": "staff_id", "RelatedTable": "staff", "RelatedColumns": "staff_id", "DeleteAction": "RESTRICT", "UpdateAction": "CASCADE" },
                { "Name": "fk_payment_rental", "Columns": "rental_id", "RelatedTable": "rental", "RelatedColumns": "rental_id", "DeleteAction": "SET NULL", "UpdateAction": "CASCADE" }
            ] }
            ]
            """;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try
        {
            using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{TestSchema}`";
            cmd.ExecuteNonQuery();
            conn.Close();
        }
        catch { /* Ignore cleanup errors */ }
    }
}
