# Course 4, Recipe 14 — Subfolder organization: your layout survives re-extraction (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (--ExitNonZeroOnWarning; #551 Product.json kept): delete the "On 2.7.x" callouts -->

Goal: extraction tools usually flatten. You organise your procedures by domain, run the tool that
refreshes them from the database, and get back one directory with two hundred files in alphabetical
order — so the organising never sticks, and after the second time nobody bothers.

SchemaTongs indexes your existing layout before it writes anything, and puts each object back where it
found it. This lab proves that on all four engines, then shows the one case where it cannot.

Everything runs against `cookbook_r14`.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r14`).
- The CLI is on your PATH: `schemaquench --version` and `schematongs --version` answer **2.7.0 or later**.

## Step 1: Look at the layout, then deploy it

Each engine's package organises procedures by business domain rather than by object type:

```
Templates/Main/Procedures/
  Billing/
    usp_invoice_total.sql
  Identity/
    usp_customer_lookup.sql
```

Nothing declares that structure — there is no manifest, no `FolderMapping`, no setting. It is just how
the files sit on disk. Deploy:

```bash
# from course4-recipe-14
cd <engine> && schemaquench --ConfigFile:deploy.settings.json ; cd ..
# exit 0 on all four engines; both procedures land in the database
```

## Step 2: Re-extract, and check nothing moved

Now pull the database back into the package — the operation that would normally flatten your work:

```bash
# from course4-recipe-14
cd <engine> && schematongs --ConfigFile:SchemaTongs.settings.json ; cd ..
find <engine>/Package/Templates/Main/Procedures -type f
```

```
Procedures/Billing/usp_invoice_total.sql
Procedures/Identity/usp_customer_lookup.sql
```

Both objects went back to their own subfolders, on SQL Server, PostgreSQL, MySQL and MariaDB. **The
assertion is `git status`, not the file listing** — if a file had moved you would see a delete and an
add, and here you see neither. On SQL Server and PostgreSQL there is nothing to see at all. On MySQL and
MariaDB the two procedures show as modified — edits in place, not moves: the extraction writes each in the form
the server returns it, with a `sql_mode` pin around it. Commit that once; what you are checking for is files
that changed folder.

> **On 2.7.x** `Product.json` is modified too, on every engine: the extraction writes the database it read into
> it as a `MainDb` script token (#551).
That stability is the whole feature: a re-extraction that churns your tree is one nobody runs.

Before it writes anything, SchemaTongs walks the template folder and builds an index of every `.sql`,
`.sqlerror` and `.json` file it finds, mapping each name to the full path it currently occupies. When
it comes to write an extracted object, it looks the filename up in that index and writes it back to
the same place.

## Step 3: New objects land in the root — on purpose

Create a procedure directly in the database that the package has never seen, then re-extract. It
appears in `Procedures/` — the folder root, not in `Billing/` or `Identity/`.

```bash
# from course4-recipe-14
../lab-sql.sh sqlserver cookbook_r14 "EXEC('CREATE PROCEDURE dbo.usp_new_report AS SELECT 1')"
../lab-sql.sh postgres cookbook_r14 'CREATE OR REPLACE PROCEDURE public.usp_new_report() LANGUAGE plpgsql AS $$ BEGIN PERFORM 1; END $$'
../lab-sql.sh mysql cookbook_r14 "CREATE PROCEDURE usp_new_report() SELECT 1"
../lab-sql.sh mariadb cookbook_r14 "CREATE PROCEDURE usp_new_report() SELECT 1"
cd <engine> && schematongs --ConfigFile:SchemaTongs.settings.json ; cd ..
find <engine>/Package/Templates/Main/Procedures -type f
```

That is the correct behaviour, and it is worth understanding rather than working around: SchemaTongs
has no way to know which domain a brand-new object belongs to, and guessing would put files somewhere
you did not choose. So new work surfaces in one predictable place where you will see it. **Move it into
the subfolder you want, and the next extraction remembers.** The index is rebuilt from disk every run,
so filing an object is a one-time act.

## Step 4: The one case it cannot resolve

Put the *same filename* in two subfolders — copy `usp_invoice_total.sql` into `Identity/` alongside
`Billing/` — and re-extract:

```bash
# from course4-recipe-14
cp <engine>/Package/Templates/Main/Procedures/Billing/*usp_invoice_total.sql <engine>/Package/Templates/Main/Procedures/Identity/
cd <engine> && schematongs --ConfigFile:SchemaTongs.settings.json ; cd ..
find <engine>/Package/Templates/Main/Procedures -type f
```

```
Found dbo.usp_invoice_total.sql in multiple subfolders:
  .\Package\Templates\Main\Procedures\Billing, .\Package\Templates\Main\Procedures\Identity
  - writing to base folder
```

Exit **0** — a warning, not a failure. The index found two candidate homes and will not pick one for
you, so the object goes to the folder root.

A pipeline that only checks the return code sails straight past that warning. To make it stop the
line, run the same extraction with `--ExitNonZeroOnWarning` — from inside the engine folder, so the
exit code you check is SchemaTongs' own and not that of a trailing `cd ..`:

```bash
# from course4-recipe-14/<engine>
schematongs --ConfigFile:SchemaTongs.settings.json --ExitNonZeroOnWarning
echo "exit $?"
cd ..
```

The warning now names the folder root as well, because the last run wrote a copy there.

Exit **1**: the run finished, and it logged a warning. The last line still reads
`Casting Completed Successfully` — the exit code is the signal. The switch never changes a failure
code; it only turns a `0` that logged a warning into a `1`. `"ExitNonZeroOnWarning": true` in the
settings file does the same.

> **On 2.7.x** the switch does not exist: the run logs a line starting `Ignored - not recognized: --ExitNonZeroOnWarning` and exits 0.

**Read what is on disk afterwards, because this is the trap:**

```
Procedures/Billing/dbo.usp_invoice_total.sql       ← stale
Procedures/Identity/dbo.usp_invoice_total.sql      ← stale
Procedures/dbo.usp_invoice_total.sql               ← the freshly extracted one
```

Three copies. The warning tells you what happened; it does **not** clean up after you. Both stale
copies are still real script files in your package, and a deploy will happily run all three. So treat
that warning as work to do, not as information — delete the duplicates, keep the one you want, and
re-extract to confirm the tree is back to two files.

## Cleanup

```bash
# from course4-recipe-14
for e in sqlserver postgres mysql mariadb; do
  ../lab-sql.sh $e cookbook_r14 "DROP PROCEDURE IF EXISTS usp_invoice_total"
  ../lab-sql.sh $e cookbook_r14 "DROP PROCEDURE IF EXISTS usp_customer_lookup"
  ../lab-sql.sh $e cookbook_r14 "DROP PROCEDURE IF EXISTS usp_new_report"
done
```

Step 3's extraction also wrote a `usp_new_report` file into `<engine>/Package/Templates/Main/Procedures/`;
delete it so the package is back to its two procedures.

## The principle

Most of this cookbook is about making the package drive the database. This recipe is about the return
trip, and the reason it matters is social rather than technical: a tool that reorganises your repo
every time you run it trains the team not to run it. Extraction stays useful — for onboarding an
existing database, for catching drift, for refreshing after someone patched production — only if it
leaves the shape of your repository alone.

So organise the package the way your team reviews code, not the way the extractor happens to emit
files. The structure is yours; SchemaTongs just remembers it.
