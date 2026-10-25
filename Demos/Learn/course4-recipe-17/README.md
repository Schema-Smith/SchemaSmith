# Course 4, Recipe 17 — Search you declare (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (MySQL/MariaDB full-text indexes outside index-only templates, #530; the re-create's drop line; SQL Server downgrade, #594; #601 and #602 notes once those fixes ship): delete the "merged, not yet released" note, and the "Pending #601" and "Pending #602" notes -->

Goal: full-text search is the index family most packages leave as a hand-run script. It has its own objects (a
catalog on SQL Server) and its own rules (one per table on SQL Server, as many as you like on MySQL and MariaDB). This
recipe declares it like any other index: deployed, left alone when nothing changed, re-created when it changes, and
dropped by absence. Then it shows the two things that sit outside the package: a MySQL **parser**, and a server that
doesn't have Full-Text Search at all.

Three engines: `mysql/`, `mariadb/` and `sqlserver/`, everything against `cookbook_r17`. PostgreSQL has no full-text
index object. Its equivalent is an ordinary GIN index over a `tsvector` expression, which the index recipes cover.

> **Merged, not yet released.** This recipe teaches SchemaSmith 2.8 behaviour, which is merged to `main` and not yet
> in a tagged release.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r17`).
- The CLI is on your PATH: `schemaquench --version` answers **2.8.0 or later**.
- Ports: MySQL **13306**, MariaDB **13307**. The SQL Server half needs a server with Full-Text Search, which the stock
  sandbox image doesn't have. Step 5 builds one.

## Step 1: Declare a full-text index (MySQL and MariaDB)

[`Article.json`](mysql/Package/Templates/Main/Tables/Article.json) declares one, beside the ordinary indexes:

```json
"FullTextIndexes": [
  { "Name": "ft_article_title", "Columns": "`Title`" }
]
```

```bash
# from course4-recipe-17
for e in mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json); done
# exit 0, both
```

```
      Create table `Article`
      Create fulltext index: `Article`.`ft_article_title`
```

Give it something to find, and search it:

```bash
# from course4-recipe-17
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r17 "INSERT INTO Article VALUES (1, 'Quenching steel', 'Plunge the hot blade into oil.'), (2, 'Choosing an anvil', 'A heavy anvil returns more of each blow; quenching comes later.'), (3, 'Forge safety', 'Wear leather.')"; done
../lab-sql.sh mysql cookbook_r17 "SELECT ArticleId FROM Article WHERE MATCH(Title) AGAINST ('quenching') ORDER BY ArticleId"
# → 1
../lab-sql.sh mariadb cookbook_r17 "SELECT ArticleId FROM Article WHERE MATCH(Title) AGAINST ('quenching') ORDER BY ArticleId"
# → 1
```

Article 2 mentions quenching too, but only in `Body`, and the index covers `Title`.

## Step 2: Re-run, and change the columns

Run the deploy again: exit 0, and nothing under `Create missing fulltext indexes`. The index matches its declaration.

Now widen it to both columns:

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Main/Tables/Article.json" set "FullTextIndexes[Name=ft_article_title].Columns" '"`Title`, `Body`"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Tables/Article.json" set "FullTextIndexes[Name=ft_article_title].Columns" '"`Title`, `Body`"' -->
```json
{ "Name": "ft_article_title", "Columns": "`Title`, `Body`" }
```

```bash
# from course4-recipe-17
for e in mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json); done
# exit 0, both
```

```
      Drop modified fulltext index: ALTER TABLE `cookbook_r17`.`Article` DROP INDEX `ft_article_title`
      Create fulltext index: `Article`.`ft_article_title`
```

A full-text index can't be altered, so a changed one is dropped and created again. Both lines are in the log. Search
both columns:

```bash
# from course4-recipe-17
../lab-sql.sh mysql cookbook_r17 "SELECT ArticleId FROM Article WHERE MATCH(Title, Body) AGAINST ('quenching') ORDER BY ArticleId"
# → 1
# → 2
```

> **Pending #602.** On MariaDB 10.4 and later the same search returns nothing today. A full-text index added to a table
> that already has one doesn't index the rows already there: an engine bug, which the deploy will work around. Until
> then, rebuild the table and the index catches up:

```bash
# from course4-recipe-17
../lab-sql.sh mariadb cookbook_r17 "SELECT ArticleId FROM Article WHERE MATCH(Title, Body) AGAINST ('quenching') ORDER BY ArticleId"
../lab-sql.sh mariadb cookbook_r17 "OPTIMIZE TABLE Article"
../lab-sql.sh mariadb cookbook_r17 "SELECT ArticleId FROM Article WHERE MATCH(Title, Body) AGAINST ('quenching') ORDER BY ArticleId"
# → 1
# → 2
```

## Step 3: An index you didn't declare

Someone adds one by hand:

```bash
# from course4-recipe-17
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r17 "CREATE FULLTEXT INDEX ft_handmade ON Article (Body)"; done
```

Deploy: exit 0, and `ft_handmade` is still there. `deploy.settings.json` has `DropIndexesRemovedFromProduct` on, but
that flag drops indexes this package used to declare, and `ft_handmade` was never in it. An index you didn't write is
someone else's until you say otherwise. Say so with `DropUnknownIndexes` (a top-level key in `deploy.settings.json`):

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/deploy.settings.json" set DropUnknownIndexes true -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/deploy.settings.json" set DropUnknownIndexes true -->
```bash
# from course4-recipe-17
for e in mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json); done
# exit 0, both:
#       Drop index not declared for this deploy: Article.ft_handmade
../lab-sql.sh mysql cookbook_r17 "SELECT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'cookbook_r17' AND INDEX_TYPE = 'FULLTEXT' GROUP BY INDEX_NAME"
# → ft_article_title
../lab-sql.sh mariadb cookbook_r17 "SELECT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'cookbook_r17' AND INDEX_TYPE = 'FULLTEXT' GROUP BY INDEX_NAME"
# → ft_article_title
```

The same two flags govern every index family. Turn `DropUnknownIndexes` back off:

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/deploy.settings.json" del DropUnknownIndexes -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/deploy.settings.json" del DropUnknownIndexes -->

## Step 4: A parser is the server's, not the package's

MySQL ships an `ngram` parser for text with no spaces between words, such as Chinese or Japanese. Declare a second
index that uses it:

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Main/Tables/Article.json" append FullTextIndexes '{"Name": "ft_article_title_ngram", "Columns": "`Title`", "Parser": "ngram"}' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Tables/Article.json" append FullTextIndexes '{"Name": "ft_article_title_ngram", "Columns": "`Title`", "Parser": "ngram"}' -->
```json
{ "Name": "ft_article_title_ngram", "Columns": "`Title`", "Parser": "ngram" }
```

```bash
# from course4-recipe-17/mysql
schemaquench --ConfigFile:deploy.settings.json        # exit 0
#       Create fulltext index: `Article`.`ft_article_title_ngram`
```

```bash
# from course4-recipe-17/mariadb
schemaquench --ConfigFile:deploy.settings.json        # exit 2
```

```
Error: Function 'ngram' is not defined
```

MariaDB ships no `ngram` parser. A parser is a server plugin, not something SchemaSmith can supply, and the deploy
fails with the engine's own error. That's the taught failure on MariaDB. Take the index back out there:

<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Tables/Article.json" remove-where FullTextIndexes "Name=ft_article_title_ngram" -->

Extract MySQL and look at what comes back:

```bash
# from course4-recipe-17/mysql
schematongs --ConfigFile:SchemaTongs.settings.json        # exit 0
```

`extracted/Templates/Main/Tables/Article.json` has both indexes, and `"Parser": "ngram"` on the second. One rule to
know: the parser is used when an index is **created**, and not compared afterwards. MySQL and MariaDB report it only in
`SHOW CREATE TABLE`, so changing `Parser` on a deployed index changes nothing. To change a parser, rename the index.

## Step 5: SQL Server, on a server that has Full-Text Search

The stock SQL Server image doesn't include Full-Text Search. The sandbox's `fts` profile builds one that does, from
[`../docker/Dockerfile.fts`](../docker/Dockerfile.fts): the same 2022 image plus Microsoft's `mssql-server-fts`
package. The build takes a few minutes the first time:

```bash
# from docker
docker compose --profile fts up -d --build sqlserver-fts
```

Wait for `docker ps` to show `learn-sqlserver-fts` as `healthy` (port **11436**), then create the database there:

```bash
# from course4-recipe-17
LEARN_CONTAINER=learn-sqlserver-fts ../lab-sql.sh sqlserver master "IF DB_ID('cookbook_r17') IS NULL CREATE DATABASE cookbook_r17"
LEARN_CONTAINER=learn-sqlserver-fts ../lab-sql.sh sqlserver master "SELECT FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')"
# → 1
```

SQL Server allows one full-text index per table, so it's a single object. It names a **catalog** and a **key index**:

```json
"FullTextIndex": {
  "FullTextCatalog": "[LibraryCatalog]",
  "KeyIndex": "[PK_Article]",
  "ChangeTracking": "AUTO",
  "Columns": "[Title],[Body]"
}
```

The catalog is a scripted object, in [`FullTextCatalogs/LibraryCatalog.sql`](sqlserver/Package/Templates/Main/FullTextCatalogs/LibraryCatalog.sql),
which runs before the tables. The key index has to be unique, on one `NOT NULL` column.

```bash
# from course4-recipe-17/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
```

```
    Quenched .\Package\Templates\Main\FullTextCatalogs\LibraryCatalog.sql
        Adding fulltext index on [dbo].[Article]
```

`ChangeTracking: AUTO` means SQL Server populates the index in the background as rows change, so give it a moment
after the insert:

```bash
# from course4-recipe-17
LEARN_CONTAINER=learn-sqlserver-fts ../lab-sql.sh sqlserver cookbook_r17 "INSERT dbo.Article VALUES (1, N'Quenching steel', N'Plunge the hot blade into oil.'), (2, N'Choosing an anvil', N'A heavy anvil returns more of each blow; quenching comes later.'), (3, N'Forge safety', N'Wear leather.')"
LEARN_CONTAINER=learn-sqlserver-fts ../lab-sql.sh sqlserver cookbook_r17 "WAITFOR DELAY '00:00:08'; SELECT ArticleId FROM dbo.Article WHERE CONTAINS((Title, Body), N'quenching') ORDER BY ArticleId"
# → 1
# → 2
```

> **Pending #601.** Run the deploy again and today it logs `Dropping fulltext index on [dbo].[Article]` and
> `Adding fulltext index on [dbo].[Article]`, on every deploy, though nothing changed. The comparison is sensitive to
> the order of `Columns`: `[Body],[Title]` (alphabetical) is left alone, `[Title],[Body]` isn't. Until the fix ships,
> list full-text columns alphabetically. On a big table each re-create is a full repopulation.

## Step 6: Catch a bad key index before it deploys

Point `KeyIndex` at `IX_Article_Title`, which isn't unique, and lint:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Article.json" set FullTextIndex.KeyIndex '"[IX_Article_Title]"' -->
```bash
# from course4-recipe-17
schemaquench --Validate --SchemaPackagePath:./sqlserver/Package        # exit 2
```

```
ERROR [SS-FT-001] Template 'Main' / Table '[Article]': Table '[Article]' names full-text KeyIndex '[IX_Article_Title]', which is not unique. SQL Server builds a full-text index only over an unfiltered unique index on a single NOT NULL column (error 7653), so the deploy fails creating it - name one of the table's declared indexes that is.
```

Put it back to `[PK_Article]`:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Article.json" set FullTextIndex.KeyIndex '"[PK_Article]"' -->

## Step 7: The same package on a server without Full-Text Search

The plain sandbox SQL Server (port 11433) has no Full-Text Search. Deploy the same package there with
[`deploy.plain.settings.json`](sqlserver/deploy.plain.settings.json):

```bash
# from course4-recipe-17/sqlserver
schemaquench --ConfigFile:deploy.plain.settings.json        # exit 1
```

```
        Full-text index skipped on [dbo].[Article]: Full-Text Search is not installed - the table deploys without it (downgraded)
```

The table deploys and the index is recorded as a downgrade, under the default `UnsupportedFeaturePolicy` of `warn`. The
exit code is 1 and the summary's outcome is `Incomplete`, with one downgrade naming `[dbo].[Article]`. It's the same
package on two servers with two outcomes, and you can see from the summary which is which. Set the policy to `fail` and
the deploy refuses instead.

## Step 8: Drop by absence

Empty the list on MySQL and MariaDB:

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Main/Tables/Article.json" set FullTextIndexes '[]' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Tables/Article.json" set FullTextIndexes '[]' -->
```bash
# from course4-recipe-17
for e in mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json); done
# exit 0, both:
#       Drop index not declared for this deploy: Article.ft_article_title
../lab-sql.sh mysql cookbook_r17 "SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'cookbook_r17' AND INDEX_TYPE = 'FULLTEXT'"
# → 0
../lab-sql.sh mariadb cookbook_r17 "SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'cookbook_r17' AND INDEX_TYPE = 'FULLTEXT'"
# → 0
```

MySQL drops `ft_article_title_ngram` too, since it left the package along with the other one.

## What you proved

- A full-text index is declared like any other: created, left alone, re-created when its columns change, dropped by
  absence, and governed by the same two drop flags.
- A parser is a server plugin. SchemaSmith uses it at create time and doesn't compare it afterwards.
- On SQL Server the catalog is a script and the key index has rules `--Validate` checks. On a server without Full-Text
  Search the index is skipped and recorded, not a failure halfway through the deploy.

## Clean up

```bash
# from course4-recipe-17
rm -rf mysql/extracted
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r17 "DROP TABLE IF EXISTS Article"; done
../lab-sql.sh sqlserver cookbook_r17 "DROP TABLE IF EXISTS dbo.Article"
```

```bash
# from docker
docker compose rm -sfv sqlserver-fts
docker image rm schemasmith-learn/mssql-fts:2022
```

Put the package files back with `git checkout -- .` (from this folder) if you want to run the recipe again.
