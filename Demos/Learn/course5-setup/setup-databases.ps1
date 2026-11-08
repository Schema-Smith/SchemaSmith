# Create + seed the Course 5 migration-track databases on each sandbox engine, or on your
# own server's single activated engine (LEARN_SERVER). Each database is the post-migration
# state a source tool would have left behind: the shared shop schema plus that tool's own
# bookkeeping table; Module 6 adds a dev/prod pair, the second carrying production-only
# drift. Re-running is safe -- all DDL is idempotent. PASS is reported only
# after a shop table is confirmed to exist (a seed that silently fails reports FAIL, never
# a false PASS).
#
# -Reset drops and recreates the databases empty before reseeding. Only databases the labs
# created are ever dropped -- see Remove-LabDatabase.
[CmdletBinding()]
param([switch] $Reset)

. "$PSScriptRoot\..\lab-sql.ps1"

$failed = $false
$labels = @{ sqlserver = 'SQL Server'; postgres = 'PostgreSQL'; mysql = 'MySQL'; mariadb = 'MariaDB' }

function Initialize-Db {
    param([string]$Engine, [string]$Db, [string]$Tracker)
    Write-Host -NoNewline ("  {0,-26} " -f $Db)
    try {
        if ($Reset) {
            $removed = Remove-LabDatabase -Engine $Engine -Database $Db
            if ($removed -eq 'refused') {
                throw "'$Db' exists but wasn't created by the labs, so it will not be dropped. Rename or move it, then re-run."
            }
        }
        Confirm-LabDatabase -Engine $Engine -Database $Db | Out-Null
        Invoke-LabSqlFile -Engine $Engine -Database $Db -Path (Join-Path $PSScriptRoot "seed/$Engine/shop.sql") | Out-Null
        if ($Tracker) { Invoke-LabSqlFile -Engine $Engine -Database $Db -Path (Join-Path $PSScriptRoot "seed/$Engine/$Tracker.sql") | Out-Null }
        $readySql = switch ($Engine) {
            'sqlserver' { "SELECT 'READY' WHERE OBJECT_ID('dbo.Customer') IS NOT NULL" }
            'postgres'  { "SELECT 1 FROM information_schema.tables WHERE table_name='customer'" }
            default     { "SELECT 1 FROM information_schema.tables WHERE table_schema='$Db' AND table_name='Customer'" }
        }
        $ready = Invoke-LabSql -Engine $Engine -Database $Db -Sql $readySql
        if ($ready -notmatch 'READY|1') { throw "seed completed but the shop schema's Customer table was not found." }
        Write-Host $(if ($Reset) { 'PASS (reset)' } else { 'PASS' })
        return $true
    } catch {
        Write-Host 'FAIL'
        Write-Host "    $($_.Exception.Message)"
        $script:failed = $true
        return $false
    }
}

# database -> extra seed file  (a source tool's bookkeeping table, or Module 6's production drift; empty = none)
$threeEngineDbs = [ordered]@{
    'shop_from_flyway'    = 'tracker_flyway'
    'shop_from_liquibase' = 'tracker_liquibase'
    'shop_from_efcore'    = 'tracker_efcore'
    'shop_from_scripts'   = 'tracker_scripts'
    'shop_brownfield_dev' = ''
    'shop_brownfield_prod' = 'prod_drift'
}

$total = 0
$parts = @()
foreach ($engine in Get-LabEngines) {
    Write-Host $labels[$engine]
    $n = 0
    foreach ($db in $threeEngineDbs.Keys) { if (Initialize-Db $engine $db $threeEngineDbs[$db]) { $n++ } }
    # DACPAC is a SQL Server technology (Course 5 Module 4 is SQL-Server-only by design); no tracker table.
    if ($engine -eq 'sqlserver') { if (Initialize-Db $engine 'shop_from_dacpac' '') { $n++ } }
    $total += $n
    $parts += "$n $($labels[$engine])"
}

Write-Host ''
if (-not $failed) {
    Write-Host "All $total databases are seeded and ready ($($parts -join ', '))."
    exit 0
} else {
    Write-Host 'One or more databases could not be seeded. Is the sandbox up (or your own server reachable)? See Demos/Learn/README.md.'
    exit 1
}
