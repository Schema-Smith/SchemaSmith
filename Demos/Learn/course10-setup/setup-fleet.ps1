# Course 10 -- stand up the mixed-version fleet the rest of the course deploys into.
#
# One package, many engine versions, all live at once. This script:
#   (a) brings up the opt-in `mixed-fleet` compose profile (floor-version PostgreSQL 12,
#       MySQL 5.7, MariaDB 10.2 running ALONGSIDE the current-tier engines);
#   (b) waits for each floor engine (and SQL Server) to report healthy;
#   (c) provisions three SQL Server tier databases on the ONE learn-sqlserver instance at
#       differing compatibility_level -- learn_2022 (160), learn_2016 (130), learn_2008 (100)
#       -- because compat level, not the binary, is what actually gates T-SQL syntax;
#   (d) confirms the `learn` database exists on each floor engine (the images auto-create it
#       from env vars; this verifies and back-fills if one somehow did not).
#
# Re-running is safe. Every step is idempotent: databases are created only if absent, and the
# ALTER ... SET COMPATIBILITY_LEVEL is a no-op when the level already matches. PASS is reported
# per tier/engine only after the target is confirmed at the intended state.
#
# Course 10's mixed fleet is SANDBOX-ONLY: a single own-server endpoint cannot be several engine
# versions at once, so there is no own-server path here (unlike the other courses' setup labs).
# This script reaches the containers directly with `docker exec`; it does not dot-source lab-sql.ps1.
[CmdletBinding()]
param()

$dockerDir = Join-Path $PSScriptRoot '..\docker'

$SqlServerContainer = 'learn-sqlserver'
$PgFloorContainer   = 'learn-postgres-12'
$MysqlFloorContainer = 'learn-mysql-57'
$MariaFloorContainer = 'learn-mariadb-102'

$script:failed = $false

# --- engine access (direct docker exec -- self-contained, no lab-sql.ps1) --------------------

function Invoke-SqlServerExec {
    param([Parameter(Mandatory)][string]$Sql)
    # 2017 ships only /opt/mssql-tools; 2019+ ship the 18 path. Self-contained by design, as in the twin.
    $sc = '/opt/mssql-tools18/bin/sqlcmd'
    docker exec $SqlServerContainer sh -c "test -x $sc" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { $sc = '/opt/mssql-tools/bin/sqlcmd' }
    $out = docker exec $SqlServerContainer $sc `
        -S localhost -U sa -P 'Learn!Passw0rd' -C -b -h -1 -W -Q "SET NOCOUNT ON; $Sql" 2>&1
    return ($out -join [Environment]::NewLine).Trim()
}

function Invoke-PgFloorExec {
    param([Parameter(Mandatory)][string]$Sql)
    $out = docker exec $PgFloorContainer psql -U postgres -d postgres -v ON_ERROR_STOP=1 -tAc $Sql 2>&1
    return ($out -join [Environment]::NewLine).Trim()
}

# MySQL 5.7 and MariaDB 10.2 both ship only the `mysql` client and both take the MYSQL_* env vars.
function Invoke-MysqlFloorExec {
    param([Parameter(Mandatory)][string]$Container, [Parameter(Mandatory)][string]$Sql)
    $out = docker exec -e MYSQL_PWD=Learn!Passw0rd $Container mysql -uroot -N -s -e $Sql 2>&1
    return ($out -join [Environment]::NewLine).Trim()
}

# --- health wait -----------------------------------------------------------------------------

function Wait-Healthy {
    param([Parameter(Mandatory)][string]$Container, [Parameter(Mandatory)][string]$Label)
    Write-Host -NoNewline ("{0,-34} " -f "waiting: $Label")
    $status = 'unknown'
    for ($i = 1; $i -le 180; $i++) {
        $status = (docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $Container 2>$null | Out-String).Trim()
        if ($status -eq 'healthy') { Write-Host 'healthy'; return $true }
        if ($i % 6 -eq 0) { Write-Host -NoNewline '.' }
        Start-Sleep -Seconds 5
    }
    Write-Host "TIMED OUT (last status: $status)"
    return $false
}

# --- provisioning ----------------------------------------------------------------------------

function Report-Pass { param([string]$Label, [string]$Detail) Write-Host ("{0,-34} PASS ({1})" -f $Label, $Detail) }
function Report-Fail {
    param([string]$Label, [string]$Detail)
    Write-Host ("{0,-34} FAIL" -f $Label)
    if ($Detail) { $Detail -split "`n" | ForEach-Object { Write-Host "    $_" } }
    $script:failed = $true
}

function Add-SqlServerTier {
    param([string]$Label, [string]$Db, [int]$Compat)
    $out = Invoke-SqlServerExec "IF DB_ID('$Db') IS NULL CREATE DATABASE [$Db]; ALTER DATABASE [$Db] SET COMPATIBILITY_LEVEL = $Compat;"
    if ($LASTEXITCODE -ne 0) { Report-Fail $Label $out; return }
    $got = (Invoke-SqlServerExec "SELECT compatibility_level FROM sys.databases WHERE name = '$Db'") -replace '\s', ''
    if ($got -eq "$Compat") { Report-Pass $Label "compat $Compat" }
    else { Report-Fail $Label "expected compatibility_level $Compat, got '$(if ($got) { $got } else { 'none' })'" }
}

function Confirm-PgFloor {
    param([string]$Label)
    $exists = (Invoke-PgFloorExec "SELECT COUNT(*) FROM pg_database WHERE datname = 'learn'") -replace '\s', ''
    if ($exists -ne '1') {
        $out = Invoke-PgFloorExec "CREATE DATABASE learn"
        if ($LASTEXITCODE -ne 0) { Report-Fail $Label $out; return }
        $exists = (Invoke-PgFloorExec "SELECT COUNT(*) FROM pg_database WHERE datname = 'learn'") -replace '\s', ''
    }
    if ($exists -eq '1') { Report-Pass $Label 'learn ready' } else { Report-Fail $Label 'learn database missing' }
}

function Confirm-MysqlFloor {
    param([string]$Label, [string]$Container)
    $out = Invoke-MysqlFloorExec -Container $Container -Sql "CREATE DATABASE IF NOT EXISTS ``learn``"
    if ($LASTEXITCODE -ne 0) { Report-Fail $Label $out; return }
    $exists = (Invoke-MysqlFloorExec -Container $Container -Sql "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = 'learn'") -replace '\s', ''
    if ($exists -eq '1') { Report-Pass $Label 'learn ready' } else { Report-Fail $Label 'learn database missing' }
}

# --- run -------------------------------------------------------------------------------------

Write-Host 'Bringing up the mixed-version fleet (docker compose --profile mixed-fleet up -d)...'
Push-Location $dockerDir
try { docker compose --profile mixed-fleet up -d } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    Write-Host 'FAIL: could not start the mixed-fleet profile. Is Docker running? See Demos/Learn/README.md.'
    exit 1
}
Write-Host ''

Write-Host 'Waiting for engines to report healthy (floor images boot in seconds; a cold SQL Server can take longer)...'
if (-not (Wait-Healthy -Container $SqlServerContainer  -Label 'SQL Server (learn-sqlserver)')) { $script:failed = $true }
if (-not (Wait-Healthy -Container $PgFloorContainer    -Label 'PostgreSQL 12 (floor)'))        { $script:failed = $true }
if (-not (Wait-Healthy -Container $MysqlFloorContainer -Label 'MySQL 5.7 (floor)'))            { $script:failed = $true }
if (-not (Wait-Healthy -Container $MariaFloorContainer -Label 'MariaDB 10.2 (floor)'))         { $script:failed = $true }
Write-Host ''

if ($script:failed) {
    Write-Host 'One or more engines never became healthy -- cannot provision. Re-run once Docker settles, or see Demos/Learn/README.md.'
    exit 1
}

Write-Host 'Provisioning SQL Server compatibility tiers on learn-sqlserver (11433)...'
Add-SqlServerTier -Label 'SQL Server tier learn_2022' -Db 'learn_2022' -Compat 160
Add-SqlServerTier -Label 'SQL Server tier learn_2016' -Db 'learn_2016' -Compat 130
Add-SqlServerTier -Label 'SQL Server tier learn_2008' -Db 'learn_2008' -Compat 100
Write-Host ''

Write-Host 'Confirming the learn database on each floor engine...'
Confirm-PgFloor    -Label 'PostgreSQL 12 floor (15433)'
Confirm-MysqlFloor -Label 'MySQL 5.7 floor (13316)'    -Container $MysqlFloorContainer
Confirm-MysqlFloor -Label 'MariaDB 10.2 floor (13317)' -Container $MariaFloorContainer
Write-Host ''

if ($script:failed) {
    Write-Host 'One or more targets could not be provisioned. See the FAIL detail above.'
    exit 1
}

@'
The mixed-version fleet is ready. One package will deploy across every tier below.

  Engine       Current tier              Floor tier (--profile mixed-fleet)
  ----------   -----------------------   ----------------------------------
  SQL Server   localhost,11433           (same instance, per-database compatibility_level)
                 learn_2022  compat 160
                 learn_2016  compat 130
                 learn_2008  compat 100
  PostgreSQL   localhost:15432  (16)     localhost:15433  (12)
  MySQL        localhost:13306  (8.0)    localhost:13316  (5.7)
  MariaDB      localhost:13307  (11.4)   localhost:13317  (10.2)

  All engines: user as in Demos/Learn/README.md, password Learn!Passw0rd, database learn
  (SQL Server tiers use learn_2022 / learn_2016 / learn_2008 instead of learn).

Next: Module 1 reads the detected version of every target with the pre-flight pair.
'@ | Write-Host
exit 0
