// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.MariaDb;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;

using Schema.Delivery;
namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class PlatformDeserializerTests
    {
        #region DeserializeTable

        [Test]
        public void DeserializeTable_SqlServer_ReturnsSqlServerTable()
        {
            var json = JsonConvert.SerializeObject(new SqlServerTable
            {
                Name = "Customer",
                Schema = "sales",
                CompressionType = "PAGE",
                IsTemporal = true
            });

            var result = PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

            Assert.That(result, Is.InstanceOf<SqlServerTable>());
            var sqlTable = (SqlServerTable)result;
            Assert.That(sqlTable.Name, Is.EqualTo("Customer"));
            Assert.That(sqlTable.Schema, Is.EqualTo("sales"));
            Assert.That(sqlTable.CompressionType, Is.EqualTo("PAGE"));
            Assert.That(sqlTable.IsTemporal, Is.True);
        }

        [Test]
        public void DeserializeTable_PostgreSQL_ReturnsPostgreSqlTable()
        {
            var json = JsonConvert.SerializeObject(new PostgreSqlTable
            {
                Name = "Customer",
                Schema = "inventory",
                RowLevelSecurity = true,
                PersistenceType = "UNLOGGED"
            });

            var result = PlatformDeserializer.DeserializeTable(json, Platform.PostgreSQL);

            Assert.That(result, Is.InstanceOf<PostgreSqlTable>());
            var pgTable = (PostgreSqlTable)result;
            Assert.That(pgTable.Name, Is.EqualTo("Customer"));
            Assert.That(pgTable.Schema, Is.EqualTo("inventory"));
            Assert.That(pgTable.RowLevelSecurity, Is.True);
            Assert.That(pgTable.PersistenceType, Is.EqualTo("UNLOGGED"));
        }

        [Test]
        public void DeserializeTable_MySQL_ReturnsMySqlTable()
        {
            var json = JsonConvert.SerializeObject(new MySqlTable
            {
                Name = "Customer",
                Engine = "MyISAM",
                RowFormat = "COMPRESSED",
                CharacterSet = "utf8mb4"
            });

            var result = PlatformDeserializer.DeserializeTable(json, Platform.MySQL);

            Assert.That(result, Is.InstanceOf<MySqlTable>());
            var myTable = (MySqlTable)result;
            Assert.That(myTable.Name, Is.EqualTo("Customer"));
            Assert.That(myTable.Engine, Is.EqualTo("MyISAM"));
            Assert.That(myTable.RowFormat, Is.EqualTo("COMPRESSED"));
            Assert.That(myTable.CharacterSet, Is.EqualTo("utf8mb4"));
        }

        [Test]
        public void DeserializeTable_MySQL_NestedColumnsAreMySqlColumn()
        {
            var json = @"{ ""Name"": ""actor"", ""Columns"": [{ ""Name"": ""actor_id"", ""DataType"": ""int"", ""AutoIncrement"": true }] }";

            var result = PlatformDeserializer.DeserializeTable(json, Platform.MySQL);

            Assert.That(result.Columns, Has.Count.EqualTo(1));
            Assert.That(result.Columns[0], Is.InstanceOf<MySqlColumn>());
            Assert.That(((MySqlColumn)result.Columns[0]).AutoIncrement, Is.True);
        }

        [Test]
        public void DeserializeTable_SqlServer_NestedColumnsAreSqlServerColumn()
        {
            var json = @"{ ""Name"": ""Customer"", ""Columns"": [{ ""Name"": ""Email"", ""DataType"": ""nvarchar(255)"", ""Sparse"": true }] }";

            var result = PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

            Assert.That(result.Columns[0], Is.InstanceOf<SqlServerColumn>());
            Assert.That(((SqlServerColumn)result.Columns[0]).Sparse, Is.True);
        }

        [Test]
        public void DeserializeTable_PostgreSQL_NestedColumnsArePostgreSqlColumn()
        {
            var json = @"{ ""Name"": ""data"", ""Columns"": [{ ""Name"": ""payload"", ""DataType"": ""jsonb"", ""Storage"": ""EXTENDED"" }] }";

            var result = PlatformDeserializer.DeserializeTable(json, Platform.PostgreSQL);

            Assert.That(result.Columns[0], Is.InstanceOf<PostgreSqlColumn>());
            Assert.That(((PostgreSqlColumn)result.Columns[0]).Storage, Is.EqualTo("EXTENDED"));
        }

        [Test]
        public void DeserializeTable_MySQL_NestedIndexesAreMySqlIndex()
        {
            var json = @"{ ""Name"": ""actor"", ""Indexes"": [{ ""Name"": ""idx_name"", ""IndexColumns"": ""name"", ""IndexType"": ""HASH"" }] }";

            var result = PlatformDeserializer.DeserializeTable(json, Platform.MySQL);

            Assert.That(result.Indexes[0], Is.InstanceOf<MySqlIndex>());
            Assert.That(((MySqlIndex)result.Indexes[0]).IndexType, Is.EqualTo("HASH"));
        }

        [Test]
        public void DeserializeTable_SqlServer_NestedForeignKeysAreSqlServerForeignKey()
        {
            var json = @"{ ""Name"": ""Order"", ""ForeignKeys"": [{ ""Name"": ""FK_Order_Customer"", ""Columns"": ""CustomerId"", ""RelatedTable"": ""Customer"", ""RelatedColumns"": ""Id"", ""RelatedTableSchema"": ""sales"" }] }";

            var result = PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

            Assert.That(result.ForeignKeys[0], Is.InstanceOf<SqlServerForeignKey>());
            Assert.That(((SqlServerForeignKey)result.ForeignKeys[0]).RelatedTableSchema, Is.EqualTo("sales"));
        }

        [Test]
        public void DeserializeTable_PostgreSQL_NestedCheckConstraintsArePostgreSqlCheckConstraint()
        {
            var json = @"{ ""Name"": ""employee"", ""CheckConstraints"": [{ ""Name"": ""ck_age"", ""Expression"": ""age >= 0"", ""Deferrable"": true }] }";

            var result = PlatformDeserializer.DeserializeTable(json, Platform.PostgreSQL);

            Assert.That(result.CheckConstraints[0], Is.InstanceOf<PostgreSqlCheckConstraint>());
            Assert.That(((PostgreSqlCheckConstraint)result.CheckConstraints[0]).Deferrable, Is.True);
        }

        [Test]
        public void DeserializeTable_UnrecognizedTopLevelProperty_Throws()
        {
            var json = @"{ ""Name"": ""Customer"", ""Bogus"": ""x"" }";

            // Assert.Catch (not Assert.Throws) — Newtonsoft may wrap the MissingMemberHandling
            // failure in an outer JsonSerializationException while adding path context, so the
            // exact runtime type isn't the point; ex.ToString() walks the full InnerException
            // chain, so the property name is found regardless of which level it landed on.
            var ex = Assert.Catch<System.Exception>(() => PlatformDeserializer.DeserializeTable(json, Platform.SqlServer));
            Assert.That(ex.ToString(), Does.Contain("Bogus"));
        }

        // PlatformItemConverter hands nested Column/Index/ForeignKey/CheckConstraint elements to a
        // second, "clean" serializer (built without this converter, to avoid recursing back into
        // itself) — the property this check guards is that the clean serializer still carries the
        // unknown-property rejection forward instead of quietly reverting to Newtonsoft's default.
        [Test]
        public void DeserializeTable_UnrecognizedNestedColumnProperty_Throws()
        {
            var json = @"{ ""Name"": ""Customer"", ""Columns"": [{ ""Name"": ""Email"", ""Bogus"": ""x"" }] }";

            var ex = Assert.Catch<System.Exception>(() => PlatformDeserializer.DeserializeTable(json, Platform.SqlServer));
            Assert.That(ex.ToString(), Does.Contain("Bogus"));
        }

        [Test]
        public void DeserializeTable_ExtensionsContent_RoundTripsUntouched()
        {
            var json = @"{ ""Name"": ""Customer"", ""Extensions"": { ""CustomFlag"": true, ""Nested"": { ""Deep"": 1 } } }";

            var result = PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

            Assert.That(result.GetExtensionProperty("CustomFlag"), Is.EqualTo(true));
        }

        [Test]
        public void DeserializeTable_BaseProperties_WorkThroughSubclass()
        {
            var json = JsonConvert.SerializeObject(new SqlServerTable
            {
                Name = "Orders",
                DataDelivery = [new DataDelivery { MergeType = "Insert/Update", MatchColumns = "OrderId", MergeDisableTriggers = true }],
                ShouldApplyExpression = "SELECT 1"
            });

            var result = PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

            Assert.That(result.Name, Is.EqualTo("Orders"));
            Assert.That(result.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
            Assert.That(result.DataDelivery[0].MatchColumns, Is.EqualTo("OrderId"));
            Assert.That(result.DataDelivery[0].MergeDisableTriggers, Is.True);
            Assert.That(result.ShouldApplyExpression, Is.EqualTo("SELECT 1"));
        }

        #endregion

        #region DeserializeColumn

        [Test]
        public void DeserializeColumn_SqlServer_ReturnsSqlServerColumn()
        {
            var json = JsonConvert.SerializeObject(new SqlServerColumn
            {
                Name = "Email",
                DataType = "nvarchar(255)",
                Collation = "Latin1_General_CI_AS",
                Sparse = true
            });

            var result = PlatformDeserializer.DeserializeColumn(json, Platform.SqlServer);

            Assert.That(result, Is.InstanceOf<SqlServerColumn>());
            var sqlCol = (SqlServerColumn)result;
            Assert.That(sqlCol.Name, Is.EqualTo("Email"));
            Assert.That(sqlCol.DataType, Is.EqualTo("nvarchar(255)"));
            Assert.That(sqlCol.Collation, Is.EqualTo("Latin1_General_CI_AS"));
            Assert.That(sqlCol.Sparse, Is.True);
        }

        [Test]
        public void DeserializeColumn_PostgreSQL_ReturnsPostgreSqlColumn()
        {
            var json = JsonConvert.SerializeObject(new PostgreSqlColumn
            {
                Name = "data",
                DataType = "jsonb",
                Storage = "EXTENDED",
                Compression = "lz4"
            });

            var result = PlatformDeserializer.DeserializeColumn(json, Platform.PostgreSQL);

            Assert.That(result, Is.InstanceOf<PostgreSqlColumn>());
            var pgCol = (PostgreSqlColumn)result;
            Assert.That(pgCol.Name, Is.EqualTo("data"));
            Assert.That(pgCol.Storage, Is.EqualTo("EXTENDED"));
            Assert.That(pgCol.Compression, Is.EqualTo("lz4"));
        }

        [Test]
        public void DeserializeColumn_MySQL_ReturnsMySqlColumn()
        {
            var json = JsonConvert.SerializeObject(new MySqlColumn
            {
                Name = "Id",
                DataType = "int",
                AutoIncrement = true,
                CharacterSet = "utf8mb4"
            });

            var result = PlatformDeserializer.DeserializeColumn(json, Platform.MySQL);

            Assert.That(result, Is.InstanceOf<MySqlColumn>());
            var myCol = (MySqlColumn)result;
            Assert.That(myCol.Name, Is.EqualTo("Id"));
            Assert.That(myCol.AutoIncrement, Is.True);
            Assert.That(myCol.CharacterSet, Is.EqualTo("utf8mb4"));
        }

        #endregion

        #region DeserializeIndex

        [Test]
        public void DeserializeIndex_SqlServer_ReturnsSqlServerIndex()
        {
            var json = JsonConvert.SerializeObject(new SqlServerIndex
            {
                Name = "IX_Customer_Name",
                IndexColumns = "LastName, FirstName",
                Clustered = true,
                FillFactor = 90
            });

            var result = PlatformDeserializer.DeserializeIndex(json, Platform.SqlServer);

            Assert.That(result, Is.InstanceOf<SqlServerIndex>());
            var sqlIdx = (SqlServerIndex)result;
            Assert.That(sqlIdx.Name, Is.EqualTo("IX_Customer_Name"));
            Assert.That(sqlIdx.Clustered, Is.True);
            Assert.That(sqlIdx.FillFactor, Is.EqualTo(90));
        }

        [Test]
        public void DeserializeIndex_PostgreSQL_ReturnsPostgreSqlIndex()
        {
            var json = JsonConvert.SerializeObject(new PostgreSqlIndex
            {
                Name = "ix_data",
                IndexColumns = "data_col",
                AccessMethod = "gin",
                NullsNotDistinct = true
            });

            var result = PlatformDeserializer.DeserializeIndex(json, Platform.PostgreSQL);

            Assert.That(result, Is.InstanceOf<PostgreSqlIndex>());
            var pgIdx = (PostgreSqlIndex)result;
            Assert.That(pgIdx.AccessMethod, Is.EqualTo("gin"));
            Assert.That(pgIdx.NullsNotDistinct, Is.True);
        }

        [Test]
        public void DeserializeIndex_MySQL_ReturnsMySqlIndex()
        {
            var json = JsonConvert.SerializeObject(new MySqlIndex
            {
                Name = "idx_name",
                IndexColumns = "name",
                IndexType = "HASH",
                Visible = false
            });

            var result = PlatformDeserializer.DeserializeIndex(json, Platform.MySQL);

            Assert.That(result, Is.InstanceOf<MySqlIndex>());
            var myIdx = (MySqlIndex)result;
            Assert.That(myIdx.IndexType, Is.EqualTo("HASH"));
            Assert.That(myIdx.Visible, Is.False);
        }

        #endregion

        #region DeserializeForeignKey

        [Test]
        public void DeserializeForeignKey_SqlServer_ReturnsSqlServerForeignKey()
        {
            var json = JsonConvert.SerializeObject(new SqlServerForeignKey
            {
                Name = "FK_Order_Customer",
                Columns = "CustomerId",
                RelatedTable = "Customer",
                RelatedColumns = "Id",
                RelatedTableSchema = "sales"
            });

            var result = PlatformDeserializer.DeserializeForeignKey(json, Platform.SqlServer);

            Assert.That(result, Is.InstanceOf<SqlServerForeignKey>());
            var sqlFk = (SqlServerForeignKey)result;
            Assert.That(sqlFk.Name, Is.EqualTo("FK_Order_Customer"));
            Assert.That(sqlFk.RelatedTableSchema, Is.EqualTo("sales"));
        }

        [Test]
        public void DeserializeForeignKey_PostgreSQL_ReturnsPostgreSqlForeignKey()
        {
            var json = JsonConvert.SerializeObject(new PostgreSqlForeignKey
            {
                Name = "fk_order_customer",
                Columns = "customer_id",
                RelatedTable = "customer",
                RelatedColumns = "id",
                Deferrable = true,
                InitiallyDeferred = true,
                MatchType = "SIMPLE"
            });

            var result = PlatformDeserializer.DeserializeForeignKey(json, Platform.PostgreSQL);

            Assert.That(result, Is.InstanceOf<PostgreSqlForeignKey>());
            var pgFk = (PostgreSqlForeignKey)result;
            Assert.That(pgFk.Deferrable, Is.True);
            Assert.That(pgFk.InitiallyDeferred, Is.True);
            Assert.That(pgFk.MatchType, Is.EqualTo("SIMPLE"));
        }

        [Test]
        public void DeserializeForeignKey_MySQL_ReturnsMySqlForeignKey()
        {
            var json = JsonConvert.SerializeObject(new MySqlForeignKey
            {
                Name = "fk_order_customer",
                Columns = "customer_id",
                RelatedTable = "customer",
                RelatedColumns = "id",
                RelatedTableSchema = "mydb"
            });

            var result = PlatformDeserializer.DeserializeForeignKey(json, Platform.MySQL);

            Assert.That(result, Is.InstanceOf<MySqlForeignKey>());
            var myFk = (MySqlForeignKey)result;
            Assert.That(myFk.RelatedTableSchema, Is.EqualTo("mydb"));
        }

        #endregion

        #region DeserializeCheckConstraint

        [Test]
        public void DeserializeCheckConstraint_SqlServer_ReturnsSqlServerCheckConstraint()
        {
            var json = JsonConvert.SerializeObject(new CheckConstraint
            {
                Name = "CK_Age",
                Expression = "Age >= 0"
            });

            var result = PlatformDeserializer.DeserializeCheckConstraint(json, Platform.SqlServer);

            Assert.That(result, Is.InstanceOf<CheckConstraint>());
            Assert.That(result.GetType(), Is.EqualTo(typeof(SqlServerCheckConstraint)));
            Assert.That(result.Name, Is.EqualTo("CK_Age"));
            Assert.That(result.Expression, Is.EqualTo("Age >= 0"));
        }

        [Test]
        public void DeserializeCheckConstraint_PostgreSQL_ReturnsPostgreSqlCheckConstraint()
        {
            var json = JsonConvert.SerializeObject(new PostgreSqlCheckConstraint
            {
                Name = "ck_age",
                Expression = "age >= 0",
                Deferrable = true,
                InitiallyDeferred = true
            });

            var result = PlatformDeserializer.DeserializeCheckConstraint(json, Platform.PostgreSQL);

            Assert.That(result, Is.InstanceOf<PostgreSqlCheckConstraint>());
            var pgCc = (PostgreSqlCheckConstraint)result;
            Assert.That(pgCc.Deferrable, Is.True);
            Assert.That(pgCc.InitiallyDeferred, Is.True);
        }

        [Test]
        public void DeserializeCheckConstraint_MySQL_ReturnsMySqlCheckConstraint()
        {
            var json = JsonConvert.SerializeObject(new CheckConstraint
            {
                Name = "ck_age",
                Expression = "age >= 0"
            });

            var result = PlatformDeserializer.DeserializeCheckConstraint(json, Platform.MySQL);

            Assert.That(result, Is.InstanceOf<CheckConstraint>());
            Assert.That(result.GetType(), Is.EqualTo(typeof(MySqlCheckConstraint)));
        }

        #endregion

        #region DeserializeTemplate

        [Test]
        public void DeserializeTemplate_SqlServer_ReturnsSqlServerTemplate()
        {
            var json = JsonConvert.SerializeObject(new SqlServerTemplate
            {
                Name = "Main",
                DatabaseIdentificationScript = "SELECT 1",
                ServerToQuench = ServerToQuench.Secondary
            });

            var result = PlatformDeserializer.DeserializeTemplate(json, Platform.SqlServer);

            Assert.That(result, Is.InstanceOf<SqlServerTemplate>());
            var sqlTmpl = (SqlServerTemplate)result;
            Assert.That(sqlTmpl.Name, Is.EqualTo("Main"));
            Assert.That(sqlTmpl.ServerToQuench, Is.EqualTo(ServerToQuench.Secondary));
        }

        [Test]
        public void DeserializeTemplate_PostgreSQL_ReturnsPostgreSqlTemplate()
        {
            var json = JsonConvert.SerializeObject(new PostgreSqlTemplate
            {
                Name = "Main",
                DatabaseIdentificationScript = "SELECT 1"
            });

            var result = PlatformDeserializer.DeserializeTemplate(json, Platform.PostgreSQL);

            Assert.That(result, Is.InstanceOf<PostgreSqlTemplate>());
            Assert.That(result.Name, Is.EqualTo("Main"));
        }

        [Test]
        public void DeserializeTemplate_MySQL_ReturnsMySqlTemplate()
        {
            var json = JsonConvert.SerializeObject(new MySqlTemplate
            {
                Name = "Main",
                DatabaseIdentificationScript = "SELECT 1"
            });

            var result = PlatformDeserializer.DeserializeTemplate(json, Platform.MySQL);

            Assert.That(result, Is.InstanceOf<MySqlTemplate>());
            Assert.That(result.Name, Is.EqualTo("Main"));
        }

        [Test]
        public void DeserializeTemplate_BaseProperties_WorkThroughSubclass()
        {
            var json = JsonConvert.SerializeObject(new SqlServerTemplate
            {
                Name = "Main",
                DatabaseIdentificationScript = "SELECT DB_NAME()",
                VersionStampScript = "EXEC SetVersion @v",
                IndexOnlyTableQuenches = true,
                UpdateFillFactor = false,
                RequireAtLeastOneTarget = false
            });

            var result = PlatformDeserializer.DeserializeTemplate(json, Platform.SqlServer);

            Assert.That(result.Name, Is.EqualTo("Main"));
            Assert.That(result.DatabaseIdentificationScript, Is.EqualTo("SELECT DB_NAME()"));
            Assert.That(result.VersionStampScript, Is.EqualTo("EXEC SetVersion @v"));
            Assert.That(result.IndexOnlyTableQuenches, Is.True);
            Assert.That(result.UpdateFillFactor, Is.False);
            Assert.That(result.RequireAtLeastOneTarget, Is.False);
        }

        #endregion

        #region GetType Methods

        [TestCase(Platform.SqlServer, typeof(SqlServerTable))]
        [TestCase(Platform.PostgreSQL, typeof(PostgreSqlTable))]
        [TestCase(Platform.MySQL, typeof(MySqlTable))]
        [TestCase(Platform.MariaDb, typeof(MariaDbTable))]
        public void GetTableType_ReturnsCorrectType(Platform platform, System.Type expectedType)
        {
            Assert.That(PlatformDeserializer.GetTableType(platform), Is.EqualTo(expectedType));
        }

        [TestCase(Platform.SqlServer, typeof(SqlServerColumn))]
        [TestCase(Platform.PostgreSQL, typeof(PostgreSqlColumn))]
        [TestCase(Platform.MySQL, typeof(MySqlColumn))]
        // MariaDb was absent here entirely, and it mapped to MySqlColumn -- so the platform that has a
        // column attribute MySQL lacks was the one platform this case did not pin. #408
        [TestCase(Platform.MariaDb, typeof(MariaDbColumn))]
        public void GetColumnType_ReturnsCorrectType(Platform platform, System.Type expectedType)
        {
            Assert.That(PlatformDeserializer.GetColumnType(platform), Is.EqualTo(expectedType));
        }

        /// <summary>
        /// MariaDB resolves to its own type, and MySQL must not follow it there. The MySQL case above is
        /// the other half of this: Type equality is exact, so it would fail if MySQL ever started
        /// resolving to the MariaDbTable subclass.
        /// <para>The leak this seam prevents -- a MariaDB-only property appearing in
        /// tables.mysql.schema -- cannot be asserted yet, because MariaDbTable declares no properties
        /// of its own and the assertion would pass vacuously. That test belongs with the first
        /// MariaDB-only property, not here.</para>
        /// </summary>
        [Test]
        public void MariaDbTable_IsAMySqlTable_SoSharedBehaviourIsInherited()
        {
            Assert.That(typeof(MariaDbTable).IsSubclassOf(typeof(MySqlTable)), Is.True,
                "MariaDB shares nearly all of its table shape with MySQL; the subclass exists to add to "
                + "that, not to replace it.");
        }

        [TestCase(Platform.SqlServer, typeof(SqlServerIndex))]
        [TestCase(Platform.PostgreSQL, typeof(PostgreSqlIndex))]
        [TestCase(Platform.MySQL, typeof(MySqlIndex))]
        public void GetIndexType_ReturnsCorrectType(Platform platform, System.Type expectedType)
        {
            Assert.That(PlatformDeserializer.GetIndexType(platform), Is.EqualTo(expectedType));
        }

        [TestCase(Platform.SqlServer, typeof(SqlServerForeignKey))]
        [TestCase(Platform.PostgreSQL, typeof(PostgreSqlForeignKey))]
        [TestCase(Platform.MySQL, typeof(MySqlForeignKey))]
        public void GetForeignKeyType_ReturnsCorrectType(Platform platform, System.Type expectedType)
        {
            Assert.That(PlatformDeserializer.GetForeignKeyType(platform), Is.EqualTo(expectedType));
        }

        [TestCase(Platform.SqlServer, typeof(SqlServerCheckConstraint))]
        [TestCase(Platform.PostgreSQL, typeof(PostgreSqlCheckConstraint))]
        [TestCase(Platform.MySQL, typeof(MySqlCheckConstraint))]
        [TestCase(Platform.MariaDb, typeof(CheckConstraint))]
        public void GetCheckConstraintType_ReturnsCorrectType(Platform platform, System.Type expectedType)
        {
            Assert.That(PlatformDeserializer.GetCheckConstraintType(platform), Is.EqualTo(expectedType));
        }

        [TestCase(Platform.SqlServer, typeof(SqlServerTemplate))]
        [TestCase(Platform.PostgreSQL, typeof(PostgreSqlTemplate))]
        [TestCase(Platform.MySQL, typeof(MySqlTemplate))]
        public void GetTemplateType_ReturnsCorrectType(Platform platform, System.Type expectedType)
        {
            Assert.That(PlatformDeserializer.GetTemplateType(platform), Is.EqualTo(expectedType));
        }

        #endregion

        #region Platform.Unknown (sentinel value)

        [Test]
        public void DeserializeTable_Unknown_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeTable("{}", Platform.Unknown),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetTableType_Unknown_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetTableType(Platform.Unknown),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetColumnType_Unknown_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetColumnType(Platform.Unknown),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetIndexType_Unknown_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetIndexType(Platform.Unknown),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetForeignKeyType_Unknown_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetForeignKeyType(Platform.Unknown),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetCheckConstraintType_Unknown_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetCheckConstraintType(Platform.Unknown),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetTemplateType_Unknown_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetTemplateType(Platform.Unknown),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        #endregion

        #region DeserializeMaterializedView

        [Test]
        public void DeserializeMaterializedView_PostgreSQL_ReturnsCorrectType()
        {
            var json = @"{""Name"":""test"",""Definition"":""SELECT 1"",""Indexes"":[{""Name"":""ix_test"",""Unique"":true,""IndexColumns"":""id"",""AccessMethod"":""btree"",""FillFactor"":90}]}";
            var result = PlatformDeserializer.DeserializeMaterializedView(json, Platform.PostgreSQL);
            Assert.That(result, Is.InstanceOf<PostgreSqlMaterializedView>());
            Assert.That(result.Name, Is.EqualTo("test"));
            Assert.That(result.Indexes, Has.Count.EqualTo(1));
            Assert.That(result.Indexes[0].AccessMethod, Is.EqualTo("btree"));
            Assert.That(result.Indexes[0].FillFactor, Is.EqualTo(90));
        }

        [Test]
        public void DeserializeMaterializedView_SqlServer_ThrowsArgumentException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeMaterializedView("{}", Platform.SqlServer),
                Throws.TypeOf<System.ArgumentException>());
        }

        [Test]
        public void DeserializeMaterializedView_MySQL_ThrowsArgumentException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeMaterializedView("{}", Platform.MySQL),
                Throws.TypeOf<System.ArgumentException>());
        }

        #endregion

        #region Unknown Platform

        [Test]
        public void DeserializeTable_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeTable("{}", (Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void DeserializeColumn_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeColumn("{}", (Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void DeserializeIndex_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeIndex("{}", (Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void DeserializeForeignKey_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeForeignKey("{}", (Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void DeserializeCheckConstraint_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeCheckConstraint("{}", (Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void DeserializeTemplate_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.DeserializeTemplate("{}", (Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetTableType_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetTableType((Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetColumnType_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetColumnType((Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetIndexType_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetIndexType((Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetForeignKeyType_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetForeignKeyType((Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetCheckConstraintType_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetCheckConstraintType((Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        [Test]
        public void GetTemplateType_UnknownPlatform_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                () => PlatformDeserializer.GetTemplateType((Platform)999),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }

        #endregion
    }
}
