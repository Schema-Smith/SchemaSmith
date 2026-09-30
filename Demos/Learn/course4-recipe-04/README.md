# Course 4, Recipe 4 — Assets that travel with the schema (lab)

Goal: ship a **binary asset** (a logo) and a **reference dataset** (category rows) *inside* the package, and
have them land correctly on every engine with no per-engine editing. The same `logo.png` becomes the
platform's own binary literal — `0x…` on SQL Server and MySQL, `E'\x…'::bytea` on PostgreSQL — automatically.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the full `Package/` (including
`Package/resources/logo.png` and `Package/resources/seed-categories.sql`) plus `deploy.settings.json`,
targeting `cookbook_r4`.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup) once — it creates `cookbook_r4`).
- The CLI is on your PATH (`schemaquench --version` → `2.7.0.0` or later).

## Step 1: Look at the two file tokens

`Product.json` defines two tokens whose values point at files under `resources/` (resolved relative to the
package):

```json
"ScriptTokens": {
  "DefaultLogo": "<*BinaryFile*>resources/logo.png",
  "SeedCategories": "<*File*>resources/seed-categories.sql"
}
```

- `<*BinaryFile*>` reads the file and emits it as the **platform-appropriate binary literal**.
- `<*File*>` reads a text file and embeds its contents **verbatim** into whatever script references the token.

The after-script `Seed Brand Assets [ALWAYS].sql` then seeds the image with `{{DefaultLogo}}` and embeds the
reference-data file with `{{SeedCategories}}` — no external file handling at deploy time.

## Step 2: Deploy

```bash
cd <engine>
schemaquench --ConfigFile:deploy.settings.json
```

## Step 3: Prove the bytes round-tripped

Read the stored image back and confirm it's byte-for-byte the file you shipped (69 bytes):

```bash
cd ..            # back to the lab folder

# SQL Server
../lab-sql.sh sqlserver cookbook_r4 "SELECT DATALENGTH(Image), LOWER(CONVERT(VARCHAR(MAX), Image, 2)) FROM dbo.BrandAssets WHERE AssetName='Default'"

# PostgreSQL
../lab-sql.sh postgres cookbook_r4 "SELECT octet_length(image), encode(image,'hex') FROM public.brandassets WHERE assetname='Default'"

# MySQL
../lab-sql.sh mysql cookbook_r4 "SELECT LENGTH(Image), LOWER(HEX(Image)) FROM cookbook_r4.BrandAssets WHERE AssetName='Default'"

# MariaDB
../lab-sql.sh mariadb cookbook_r4 "SELECT LENGTH(Image), LOWER(HEX(Image)) FROM cookbook_r4.BrandAssets WHERE AssetName='Default'"
```

All four return length `69` and the same hex (`89504e47…ae426082` — the PNG signature and bytes of
`resources/logo.png`). And the reference data is in:

```bash
../lab-sql.sh postgres cookbook_r4 "SELECT string_agg(categoryname,',' ORDER BY categoryname) FROM public.category"
# → Books,Electronics,Garden
```

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Binary column | `VARBINARY(MAX)` | `BYTEA` | `BLOB` |
| `<*BinaryFile*>` literal | `0x89504E47…` | `E'\\x89504E47…'::bytea` | `0x89504E47…` |
| Read bytes back | `CONVERT(…, 2)` | `encode(…, 'hex')` | `HEX(…)` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

You author one `<*BinaryFile*>` token; the resolver picks the right literal form for each target. The `.png`
in the package is identical across all four engine folders — only the emitted SQL literal differs.

## The principle

A logo, a certificate, a signing key, a static reference set — content that belongs *with* the schema but
isn't schema. `<*BinaryFile*>` and `<*File*>` let it ride inside the package and land at deploy time as
whatever the target accepts, so the same package seeds the same assets into SQL Server, PostgreSQL, MySQL,
and MariaDB with nothing hand-edited per engine. The asset travels with the metal.
