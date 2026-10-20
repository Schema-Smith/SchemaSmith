# Module 4 — Bring an existing database under management (lab)

Goal: take a database that nobody has under source control, point **SchemaTongs** at it, and cast it
into a schema package — tables, keys, and indexes — that you then own and manage like any other.

Each engine folder has:
- `seed.sql` — plain DDL (+ a little data) that stands up a small `chinook` music-catalog database.
  This is **not** a SchemaSmith package; it's the "legacy" database you're bringing under management.
- `tongs.settings.json` — the SchemaTongs config: where to connect and where to write the package.
- `solution/` — the package SchemaTongs produces, to compare your extraction against.

It's a trimmed slice of the classic Chinook schema: `Artist`, `Album`, `Track`, `Genre`,
`MediaType`, `Playlist`, and `PlaylistTrack` (7 tables, real foreign keys).

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`).
- The CLI is on your PATH (`schematongs --version` answers **2.7.0 or later**).

## Step 1: Stand up the "existing" database

Load `seed.sql` into the sandbox. It creates a `chinook` database and the seven tables.

```bash
# from module-04
# SQL Server
../lab-sql.sh sqlserver master --file sqlserver/seed.sql

# PostgreSQL  (the seed CREATEs the chinook database, then \c switches into it)
../lab-sql.sh postgres learn --file postgres/seed.sql

# MySQL
../lab-sql.sh mysql information_schema --file mysql/seed.sql

# MariaDB
../lab-sql.sh mariadb information_schema --file mariadb/seed.sql
```

Spot-check it's there — seven tables, none of them yours yet:

```bash
# from module-04
../lab-sql.sh sqlserver chinook "SELECT name FROM sys.tables"
../lab-sql.sh postgres chinook "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'"
../lab-sql.sh mysql chinook "SELECT table_name FROM information_schema.tables WHERE table_schema = 'chinook'"
../lab-sql.sh mariadb chinook "SELECT table_name FROM information_schema.tables WHERE table_schema = 'chinook'"
```

## Step 2: Cast it into a package

From your engine folder:

```bash
# from module-04/<engine>
schematongs --ConfigFile:tongs.settings.json
```

Expected on SQL Server and PostgreSQL (SQL Server shown; PostgreSQL prints `Cast Json for public.album`):

```
  Cast Json for dbo.Album
  Cast Json for dbo.Artist
  ... (and the rest)

=== Casting Summary ===
  Tables:     7 extracted, 0 errors
Casting Completed Successfully
```

On MySQL and MariaDB the table lines are numbered instead:

```
Casting Table Structures (7 of 7 tables)
  [1/7] Extracting Album...
  [2/7] Extracting Artist...
  ... (and the rest)

=== Casting Summary ===
  Tables:     7 extracted, 0 errors
Casting Completed Successfully
```

SchemaTongs writes the package to `./Extracted`, one file per table:

```
Extracted/
  Product.json
  Templates/Chinook/Tables/dbo.Album.json     # SQL Server; PostgreSQL writes album.json
  Templates/Chinook/Tables/dbo.Artist.json    # PostgreSQL: artist.json
  ... (one file per table)
```

## Step 3: See what it caught

Open the `Album` file in `Extracted/Templates/Chinook/Tables/`. SchemaTongs didn't just record columns —
it pulled the **primary key**, the **index** on the artist column, and the **foreign key** linking
`Album` back to `Artist`, straight from the live catalog. Those relationships are the first thing you'd
miss transcribing a schema by hand.

Per-engine differences are cosmetic: SQL Server quotes with `[brackets]` and reports `INT IDENTITY`;
PostgreSQL folds names to lowercase (`album.json`) and reports types like `int4`; MySQL quotes
with `` `backticks` `` and records `AutoIncrement` plus the storage engine. MariaDB matches MySQL's
quoting and casing here — it's a fourth platform in the MySQL family, not the MySQL package retargeted.

## Step 4: Compare against the solution

Your `Extracted/` package should match `solution/`. From here, it's a normal SchemaSmith package — you
manage it exactly like the one you built in Module 2: edit a table, preview with WhatIf, and quench.

## Reset

To start over, drop the database and re-run the seed:

```bash
# from module-04
# SQL Server
../lab-sql.sh sqlserver master "DROP DATABASE chinook"
# PostgreSQL
../lab-sql.sh postgres learn "DROP DATABASE chinook"
# MySQL
../lab-sql.sh mysql information_schema "DROP DATABASE chinook"
# MariaDB
../lab-sql.sh mariadb information_schema "DROP DATABASE chinook"
```
