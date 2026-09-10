# Course 4, Recipe 12 — Declared, not scripted: scheduled events (lab)

Goal: the companion to [Recipe 11](../course4-recipe-11), on the other side of the family. MySQL and
MariaDB **scheduled events** can be declared — compared, converged, and removed by absence — instead
of scripted. And the scripted form fails in exactly the same silent way: a guarded `CREATE EVENT IF NOT
EXISTS` stops doing anything the moment the event exists, so every later edit to the body is inert
while the run keeps reporting success.

An inert event is worse than an inert domain. A domain that never tightened its CHECK lets bad rows
in. An event that never picked up your new retention window keeps **deleting on the old one**, nightly,
forever.

Both engines: `mysql/` and `mariadb/`, everything against `cookbook_r12`.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r12`).
- The CLI is on your PATH: `schemaquench --version` answers **2.6.0 or later**.
- Ports: MySQL **13306**, MariaDB **13307**. Each engine folder's `deploy.settings.json` already points
  at the right one.

## Step 1: Watch the scripted form lie — on both engines

[`scripted-before/purge_old_sessions.sql`](scripted-before/purge_old_sessions.sql) is the guarded form:

```sql
CREATE EVENT IF NOT EXISTS purge_old_sessions
  ON SCHEDULE EVERY 1 DAY
  DO DELETE FROM app_session WHERE started_at < NOW() - INTERVAL 30 DAY;
```

Give it a table to talk about, run it, and look:

```bash
for e in mysql mariadb; do
  ../lab-sql.sh $e cookbook_r12 "CREATE TABLE IF NOT EXISTS app_session (session_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, started_at DATETIME NOT NULL)"
  ../lab-sql.sh $e cookbook_r12 "$(cat scripted-before/purge_old_sessions.sql)"
  ../lab-sql.sh $e cookbook_r12 "SELECT EVENT_DEFINITION FROM information_schema.EVENTS WHERE EVENT_NAME='purge_old_sessions'"
done
# → DELETE FROM app_session WHERE started_at < NOW() - INTERVAL 30 DAY   (both engines)
```

Now **shorten the retention window**. Edit the file: `INTERVAL 30 DAY` → `INTERVAL 7 DAY`. That is a real
policy change, the kind that goes through review because someone decided you keep less data. Run the
same file again, then look again:

```
mysql    deployed = DELETE FROM app_session WHERE started_at < NOW() - INTERVAL 30 DAY
mariadb  deployed = DELETE FROM app_session WHERE started_at < NOW() - INTERVAL 30 DAY
```

Still 30. Both engines. No error, no warning — the statement is a no-op by design once the event
exists, and `IF NOT EXISTS` is precisely what makes it safe to re-run *and* deaf to every edit. Your
approved retention change is live in git and absent from the database.

Clear it before moving on:

```bash
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r12 "DROP EVENT IF EXISTS purge_old_sessions"; done
```

## Step 2: Declare the events instead

Each engine package carries an `Events/` folder beside `Tables/`:

```
Templates/Main/
  Events/purge_old_sessions.json      # EVERY 1 DAY  -- the retention sweep
  Events/archive_old_sessions.json    # EVERY 7 DAY  -- a weekly archive sweep
  Tables/app_session.json
  Tables/app_session_archive.json
```

An event declaration is its schedule plus its body:

```json
{
  "Name": "purge_old_sessions",
  "ScheduleType": "EVERY",
  "Interval": "1 DAY",
  "Status": "ENABLE",
  "Definition": "DELETE FROM app_session WHERE started_at < NOW() - INTERVAL 30 DAY"
}
```

`ScheduleType` is `EVERY` (recurring, with `Interval`) or `AT` (once, with `ExecuteAt`). Deploy both
engines:

```bash
for e in mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json); done
# exit 0, both. Run it a second time -- still exit 0, nothing changes. It is idempotent.
```

## Step 3: Change the body — this time it takes

Make the same retention edit you made in Step 1, but in the declaration: `INTERVAL 30 DAY` →
`INTERVAL 7 DAY` in `purge_old_sessions.json`. Re-quench and look:

```bash
for e in mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json); done
#   Quenching scheduled events
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r12 "SELECT EVENT_DEFINITION FROM information_schema.EVENTS WHERE EVENT_NAME='purge_old_sessions'"; done
# → DELETE FROM app_session WHERE started_at < NOW() - INTERVAL 7 DAY   (both engines)
```

Seven days, on both engines. Identical edit to Step 1, opposite result — because nothing is guarding
the declaration against being read. Put it back to 30 and re-quench, and it goes back.

## Step 4: Remove one — and watch what is *not* removed

Drop-by-absence is opt-in. Turn it on at the environment level in `deploy.settings.json`:

```json
{ "DropEventsRemovedFromProduct": true }
```

> **Environment level only.** Unlike most drop-control flags, this one is not settable per product or
> per template — it is read from the root of the settings file (or the matching environment variable).

Now prove both halves. First, plant an event SchemaSmith did **not** create:

```bash
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r12 "CREATE EVENT IF NOT EXISTS handmade_probe ON SCHEDULE EVERY 1 DAY DO SELECT 1"; done
```

Then delete `Events/archive_old_sessions.json` from both packages and re-quench:

```bash
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r12 "SELECT GROUP_CONCAT(EVENT_NAME ORDER BY EVENT_NAME) FROM information_schema.EVENTS WHERE EVENT_SCHEMA='cookbook_r12'"; done
# → handmade_probe,purge_old_sessions
```

`archive_old_sessions` is gone — the package stopped declaring it, so the deploy removed it.
`handmade_probe` is untouched, because SchemaSmith never created it. Removal reaches only what the
product owns; an event some DBA wrote at 2am is not yours to delete, and the tool does not pretend
otherwise. Restore the file and re-quench to bring the archive sweep back.

<!-- TRAINING-RELEASE-PIN #415 -- on 2.7.0, delete this note; Step 4 can then remove the only declared
     event rather than one of two. Verified fixed on main 2026-09-10. -->
> **Known limitation on 2.6.0, fixed on `main` and shipping in 2.7.0.** Removing the **last** declared
> event does not drop it on 2.6.0 — an empty `Events/` folder skips the by-absence comparison entirely,
> so the event stays deployed. That is why this step removes one of two rather than the only one.
> Reported from this lab and fixed; on 2.7.0 an empty `Events/` folder means "declare none", and
> ownership still holds — a hand-created event survives it.

## Cleanup

```bash
for e in mysql mariadb; do
  ../lab-sql.sh $e cookbook_r12 "DROP EVENT IF EXISTS purge_old_sessions; DROP EVENT IF EXISTS archive_old_sessions; DROP EVENT IF EXISTS handmade_probe; DROP TABLE IF EXISTS app_session_archive; DROP TABLE IF EXISTS app_session"
done
```

## The principle

Same as [Recipe 11](../course4-recipe-11), aimed at a different object on a different engine family —
which is the point. A guarded `CREATE` is not a declaration; it is an instruction that expires the
first time it runs and then spends years pretending otherwise. A declaration has no expiry, because
nothing protects it from being compared. The scripted event kept deleting on last year's retention
window and told you it was fine. The declared one changes when the file changes, disappears when the
file disappears, and leaves alone the things that were never yours.
