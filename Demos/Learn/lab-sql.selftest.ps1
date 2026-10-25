# Self-test for lab-sql.ps1. Requires the Learn sandbox to be up (docker compose up -d
# in Demos/Learn/docker) and sqlcmd on PATH for the own-server checks.
# Run: .\lab-sql.selftest.ps1
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\lab-sql.ps1"

$failed = 0
function Assert-Equal($Expected, $Actual, $What) {
    if ("$Expected" -eq "$Actual") { Write-Host "  PASS  $What" }
    else { Write-Host "  FAIL  $What (expected '$Expected', got '$Actual')"; $script:failed++ }
}
function Assert-Throws($Script, $Match, $What) {
    try { & $Script | Out-Null; Write-Host "  FAIL  $What (no error raised)"; $script:failed++ }
    catch {
        if ("$_" -match $Match) { Write-Host "  PASS  $What" }
        else { Write-Host "  FAIL  $What (message was '$_')"; $script:failed++ }
    }
}
function Clear-LabEnv {
    foreach ($v in 'LEARN_SERVER', 'LEARN_PORT', 'LEARN_USER', 'LEARN_PASSWORD', 'LEARN_ENGINE') {
        Remove-Item "Env:$v" -ErrorAction SilentlyContinue
    }
}
function Set-LabEnv($Engine, $Port) {
    $env:LEARN_SERVER = 'localhost'; $env:LEARN_PORT = "$Port"; $env:LEARN_ENGINE = $Engine
    $env:LEARN_USER = @{ sqlserver = 'sa'; postgres = 'postgres'; mysql = 'root'; mariadb = 'root' }[$Engine]
    $env:LEARN_PASSWORD = 'Learn!Passw0rd'
}

Write-Host 'sandbox mode'
Clear-LabEnv
Assert-Equal $false (Test-LabOwnServer) 'no LEARN_SERVER means sandbox mode'
Assert-Equal 'sqlserver postgres mysql mariadb' ((Get-LabEngines) -join ' ') 'sandbox runs all four engines'
Assert-Equal 'learn-sqlserver' (Get-LabContainer sqlserver) 'the engine names its default container'
$env:LEARN_CONTAINER = 'learn-sqlserver-2025'
Assert-Equal 'learn-sqlserver-2025' (Get-LabContainer sqlserver) 'LEARN_CONTAINER overrides the container'
Remove-Item Env:LEARN_CONTAINER
Assert-Equal 'learn-sqlserver' (Get-LabContainer sqlserver) 'clearing LEARN_CONTAINER restores the default'
Assert-Equal '1' (Invoke-LabSql -Engine sqlserver -Database master   -Sql 'SELECT 1') 'sqlserver inline query'
Assert-Equal '1' (Invoke-LabSql -Engine postgres  -Database postgres -Sql 'SELECT 1') 'postgres inline query'
Assert-Equal '1' (Invoke-LabSql -Engine mysql     -Database learn    -Sql 'SELECT 1') 'mysql inline query'
Assert-Equal '1' (Invoke-LabSql -Engine mariadb   -Database learn    -Sql 'SELECT 1') 'mariadb inline query'

Write-Host 'own-server mode (pointed at the sandbox''s published ports)'
Set-LabEnv 'sqlserver' 11433
Assert-Equal $true (Test-LabOwnServer) 'LEARN_SERVER means own-server mode'
Assert-Equal 'sqlserver' ((Get-LabEngines) -join ' ') 'own-server runs only the activated engine'
Assert-Equal '1' (Invoke-LabSql -Engine sqlserver -Database master -Sql 'SELECT 1') 'sqlserver local-client query'

Write-Host 'failures are loud'
Set-LabEnv 'sqlserver' 11499   # nothing listening
Assert-Throws { Invoke-LabSql -Engine sqlserver -Database master -Sql 'SELECT 1' } `
    'LAB-SQL: sqlserver at .* failed' 'unreachable endpoint raises, never returns text'
Set-LabEnv 'sqlserver' 11433
Assert-Throws { Invoke-LabSql -Engine sqlserver -Database master -Sql 'SELECT * FROM no_such_table_here' } `
    'LAB-SQL: sqlserver at .* failed' 'a failed query raises rather than returning the error text'

Clear-LabEnv
Write-Host 'sql files'
$tmp = Join-Path $env:TEMP 'lab-sql-selftest.sql'
'CREATE TABLE selftest_marker (id INT);' | Set-Content -Path $tmp -Encoding ASCII
Invoke-LabSql -Engine sqlserver -Database master -Sql "IF DB_ID('labselftest') IS NULL CREATE DATABASE [labselftest]" | Out-Null
Invoke-LabSqlFile -Engine sqlserver -Database labselftest -Path $tmp
Assert-Equal '1' (Invoke-LabSql -Engine sqlserver -Database labselftest `
        -Sql "SELECT COUNT(*) FROM sys.tables WHERE name = 'selftest_marker'") 'sql file executed'
Assert-Throws { Invoke-LabSqlFile -Engine sqlserver -Database labselftest -Path 'C:\nope\missing.sql' } `
    'no such SQL file' 'missing sql file raises'

Invoke-LabSql -Engine sqlserver -Database master `
    -Sql "ALTER DATABASE [labselftest] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [labselftest];" | Out-Null
Remove-Item $tmp -ErrorAction SilentlyContinue

Write-Host 'ownership guard (every engine — the stamp SQL differs per engine)'
$ports = @{ sqlserver = 11433; postgres = 15432; mysql = 13306; mariadb = 13307 }
function Remove-LabTestDb($Engine, $Db) {
    $admin = Get-LabAdminDatabase $Engine
    $sql = switch ($Engine) {
        'sqlserver' { "IF DB_ID('$Db') IS NOT NULL BEGIN ALTER DATABASE [$Db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Db]; END" }
        'postgres'  { "DROP DATABASE IF EXISTS ""$Db""" }
        default     { "DROP DATABASE IF EXISTS ``$Db``" }
    }
    Invoke-LabSql -Engine $Engine -Database $admin -Sql $sql | Out-Null
}
foreach ($e in 'sqlserver', 'postgres', 'mysql', 'mariadb') {
    Clear-LabEnv
    Remove-LabTestDb $e 'labguardtest'; Remove-LabTestDb $e 'labguardforeign'

    Assert-Equal 'created' (Confirm-LabDatabase -Engine $e -Database 'labguardtest') "$e`: creates and stamps"
    Assert-Equal 'reused'  (Confirm-LabDatabase -Engine $e -Database 'labguardtest') "$e`: reuses the stamped database"

    # An unstamped database, as a learner's real one would be.
    $admin = Get-LabAdminDatabase $e
    $create = switch ($e) {
        'sqlserver' { "CREATE DATABASE [labguardforeign]" }
        'postgres'  { "CREATE DATABASE ""labguardforeign""" }
        default     { "CREATE DATABASE ``labguardforeign``" }
    }
    Invoke-LabSql -Engine $e -Database $admin -Sql $create | Out-Null

    if (Get-Command @{ sqlserver = 'sqlcmd'; postgres = 'psql'; mysql = 'mysql'; mariadb = 'mariadb' }[$e] -ErrorAction SilentlyContinue) {
        Set-LabEnv $e $ports[$e]
        Assert-Throws { Confirm-LabDatabase -Engine $e -Database 'labguardforeign' } `
            'was NOT created by the labs' "$e`: own-server refuses an unstamped database"
        Clear-LabEnv
    }
    else { Write-Host "  SKIP  $e`: own-server refusal (client not on PATH)" }

    Assert-Equal 'reused' (Confirm-LabDatabase -Engine $e -Database 'labguardforeign') "$e`: sandbox adopts and stamps"
    Remove-LabTestDb $e 'labguardtest'; Remove-LabTestDb $e 'labguardforeign'
}

Write-Host ''
if ($failed -eq 0) { Write-Host 'lab-sql self-test: all checks passed'; exit 0 }
Write-Host "lab-sql self-test: $failed check(s) failed"
exit 1
