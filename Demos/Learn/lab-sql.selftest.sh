#!/usr/bin/env bash
# Self-test for lab-sql.sh. Requires the Learn sandbox to be up (docker compose up -d
# in Demos/Learn/docker) and sqlcmd on PATH for the own-server checks.
# Run: ./lab-sql.selftest.sh
set -u
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lab-sql.sh"

failed=0

assert_equal() {   # expected actual what
  if [ "$1" = "$2" ]; then echo "  PASS  $3"
  else echo "  FAIL  $3 (expected '$1', got '$2')"; failed=$((failed + 1)); fi
}
assert_fails() {   # match what -- command...
  local match="$1" what="$2"; shift 3
  local out rc
  out=$("$@" 2>&1); rc=$?
  if [ "$rc" -eq 0 ]; then echo "  FAIL  $what (no error raised)"; failed=$((failed + 1))
  elif echo "$out" | grep -q "$match"; then echo "  PASS  $what"
  else echo "  FAIL  $what (message was '$out')"; failed=$((failed + 1)); fi
}
clear_lab_env() { unset LEARN_SERVER LEARN_PORT LEARN_USER LEARN_PASSWORD LEARN_ENGINE || true; }
set_lab_env() {   # engine port
  export LEARN_SERVER=localhost LEARN_PORT="$2" LEARN_ENGINE="$1" LEARN_PASSWORD='Learn!Passw0rd'
  case "$1" in
    sqlserver) export LEARN_USER=sa ;;
    postgres)  export LEARN_USER=postgres ;;
    *)         export LEARN_USER=root ;;
  esac
}

echo 'sandbox mode'
clear_lab_env
if lab_own_server; then assert_equal 'sandbox' 'own-server' 'no LEARN_SERVER means sandbox mode'
else echo '  PASS  no LEARN_SERVER means sandbox mode'; fi
assert_equal 'sqlserver postgres mysql mariadb' "$(lab_engines | tr '\n' ' ' | sed 's/ $//')" 'sandbox runs all four engines'
assert_equal '1' "$(lab_sql sqlserver master   'SELECT 1')" 'sqlserver inline query'
assert_equal '1' "$(lab_sql postgres  postgres 'SELECT 1')" 'postgres inline query'
assert_equal '1' "$(lab_sql mysql     learn    'SELECT 1')" 'mysql inline query'
assert_equal '1' "$(lab_sql mariadb   learn    'SELECT 1')" 'mariadb inline query'

echo "own-server mode (pointed at the sandbox's published ports)"
set_lab_env sqlserver 11433
if lab_own_server; then echo '  PASS  LEARN_SERVER means own-server mode'
else assert_equal 'own-server' 'sandbox' 'LEARN_SERVER means own-server mode'; fi
assert_equal 'sqlserver' "$(lab_engines | tr '\n' ' ' | sed 's/ $//')" 'own-server runs only the activated engine'
assert_equal '1' "$(lab_sql sqlserver master 'SELECT 1')" 'sqlserver local-client query'

echo 'failures are loud'
set_lab_env sqlserver 11499   # nothing listening
assert_fails 'LAB-SQL: sqlserver at .* failed' 'unreachable endpoint fails, never returns text' -- \
  lab_sql sqlserver master 'SELECT 1'
set_lab_env sqlserver 11433
assert_fails 'LAB-SQL: sqlserver at .* failed' 'a failed query fails rather than returning the error text' -- \
  lab_sql sqlserver master 'SELECT * FROM no_such_table_here'

clear_lab_env
echo 'sql files'
tmp="${TMPDIR:-/tmp}/lab-sql-selftest.sql"
printf 'CREATE TABLE selftest_marker (id INT);\n' > "$tmp"
lab_sql sqlserver master "IF DB_ID('labselftestsh') IS NULL CREATE DATABASE [labselftestsh]" >/dev/null
lab_sql_file sqlserver labselftestsh "$tmp"
assert_equal '1' "$(lab_sql sqlserver labselftestsh "SELECT COUNT(*) FROM sys.tables WHERE name = 'selftest_marker'")" 'sql file executed'
assert_fails 'no such SQL file' 'missing sql file fails' -- lab_sql_file sqlserver labselftestsh '/nope/missing.sql'
lab_sql sqlserver master "ALTER DATABASE [labselftestsh] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [labselftestsh];" >/dev/null
rm -f "$tmp"

echo 'ownership guard (every engine - the stamp SQL differs per engine)'
lab_port() {
  case "$1" in
    sqlserver) printf '11433' ;; postgres) printf '15432' ;;
    mysql) printf '13306' ;; mariadb) printf '13307' ;;
  esac
}
remove_lab_test_db() {   # engine db
  local admin sql
  admin="$(lab_admin_db "$1")"
  case "$1" in
    sqlserver) sql="IF DB_ID('$2') IS NOT NULL BEGIN ALTER DATABASE [$2] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$2]; END" ;;
    postgres)  sql="DROP DATABASE IF EXISTS \"$2\"" ;;
    *)         sql="DROP DATABASE IF EXISTS \`$2\`" ;;
  esac
  lab_sql "$1" "$admin" "$sql" >/dev/null
}
for e in sqlserver postgres mysql mariadb; do
  clear_lab_env
  remove_lab_test_db "$e" labguardtestsh; remove_lab_test_db "$e" labguardforeignsh

  assert_equal 'created' "$(lab_confirm_db "$e" labguardtestsh)" "$e: creates and stamps"
  assert_equal 'reused'  "$(lab_confirm_db "$e" labguardtestsh)" "$e: reuses the stamped database"

  admin="$(lab_admin_db "$e")"
  case "$e" in
    sqlserver) create="CREATE DATABASE [labguardforeignsh]" ;;
    postgres)  create="CREATE DATABASE \"labguardforeignsh\"" ;;
    *)         create="CREATE DATABASE \`labguardforeignsh\`" ;;
  esac
  lab_sql "$e" "$admin" "$create" >/dev/null

  if lab_client "$e" >/dev/null 2>&1; then
    set_lab_env "$e" "$(lab_port "$e")"
    assert_fails 'was NOT created by the labs' "$e: own-server refuses an unstamped database" -- \
      lab_confirm_db "$e" labguardforeignsh
    clear_lab_env
  else
    echo "  SKIP  $e: own-server refusal (client not on PATH)"
  fi

  assert_equal 'reused' "$(lab_confirm_db "$e" labguardforeignsh)" "$e: sandbox adopts and stamps"
  remove_lab_test_db "$e" labguardtestsh; remove_lab_test_db "$e" labguardforeignsh
done

clear_lab_env
echo
if [ "$failed" -eq 0 ]; then echo 'lab-sql self-test: all checks passed'; exit 0; fi
echo "lab-sql self-test: $failed check(s) failed"
exit 1
