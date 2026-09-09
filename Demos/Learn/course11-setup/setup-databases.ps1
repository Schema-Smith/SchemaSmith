# Create the Course 11 "Vault" databases on each sandbox engine, or on your own server's single
# activated engine (LEARN_SERVER): vault_m1 .. vault_m4, one per module, all empty.
#
# It also creates the SQL Server partition function and schemes, and the PostgreSQL tablespace, that
# Module 1 declares BY NAME. SchemaSmith never creates either -- they are server-side objects a DBA
# owns -- and that asymmetry is Module 1's first lesson, so seeding them here is part of the teaching.
#
# Re-running is safe. -Reset drops and recreates the databases empty; only a database the labs
# created is ever dropped.
param([switch]$Reset)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\lab-sql.ps1"

$dbs = @('vault_m1','vault_m2','vault_m3','vault_m4')
$labels = @{ sqlserver = 'SQL Server'; postgres = 'PostgreSQL'; mysql = 'MySQL'; mariadb = 'MariaDB' }
$fail = $false

function Seed-SqlServerPartitioning {
    Invoke-LabSql -Engine sqlserver -Database 'vault_m1' -Sql @'
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'pf_vault_year')
  CREATE PARTITION FUNCTION pf_vault_year (INT) AS RANGE RIGHT FOR VALUES (2024, 2025, 2026);
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'ps_vault_year')
  CREATE PARTITION SCHEME ps_vault_year AS PARTITION pf_vault_year ALL TO ([PRIMARY]);
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'ps_vault_year_alt')
  CREATE PARTITION SCHEME ps_vault_year_alt AS PARTITION pf_vault_year ALL TO ([PRIMARY]);
'@ | Out-Null
    $n = (Invoke-LabSql -Engine sqlserver -Database 'vault_m1' -Sql "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.partition_schemes WHERE name IN ('ps_vault_year','ps_vault_year_alt')") -replace '\D',''
    return ($n -eq '2')
}

function Seed-PostgresTablespace {
    # CREATE TABLESPACE has no IF NOT EXISTS, so check first rather than swallowing the error --
    # a swallowed failure and an already-present tablespace must not look the same.
    $have = (Invoke-LabSql -Engine postgres -Database 'postgres' -Sql "SELECT COUNT(*) FROM pg_tablespace WHERE spcname = 'vault_ts'") -replace '\D',''
    if ($have -ne '1') {
        Invoke-LabSql -Engine postgres -Database 'postgres' -Sql "COPY (SELECT 1) TO PROGRAM 'mkdir -p /var/lib/postgresql/vault_ts'" | Out-Null
        Invoke-LabSql -Engine postgres -Database 'postgres' -Sql "CREATE TABLESPACE vault_ts LOCATION '/var/lib/postgresql/vault_ts'" | Out-Null
    }
    $n = (Invoke-LabSql -Engine postgres -Database 'postgres' -Sql "SELECT COUNT(*) FROM pg_tablespace WHERE spcname = 'vault_ts'") -replace '\D',''
    return ($n -eq '1')
}

foreach ($engine in (Get-LabEngines)) {
    Write-Host $labels[$engine]
    foreach ($db in $dbs) {
        Write-Host -NoNewline ("  {0,-24} " -f $db)
        $ok = $true; $err = ''
        if ($Reset) {
            if ((Remove-LabDatabase -Engine $engine -Database $db) -eq 'refused') {
                $err = "'$db' exists but wasn't created by the labs, so it will not be dropped."
                $ok = $false
            }
        }
        if ($ok) {
            try { Confirm-LabDatabase -Engine $engine -Database $db | Out-Null } catch { $ok = $false; $err = $_.Exception.Message }
        }
        if ($ok -and $engine -eq 'sqlserver' -and $db -eq 'vault_m1') {
            if (-not (Seed-SqlServerPartitioning)) { $ok = $false; $err = 'partition function/scheme could not be confirmed.' }
        }
        if ($ok -and $engine -eq 'postgres' -and $db -eq 'vault_m1') {
            if (-not (Seed-PostgresTablespace)) { $ok = $false; $err = 'vault_ts tablespace could not be confirmed.' }
        }
        if ($ok) {
            if ($engine -eq 'sqlserver' -and $db -eq 'vault_m1') { Write-Host 'PASS (+ pf_vault_year / ps_vault_year / ps_vault_year_alt)' }
            elseif ($engine -eq 'postgres' -and $db -eq 'vault_m1') { Write-Host 'PASS (+ vault_ts tablespace)' }
            else { Write-Host 'PASS' }
        } else {
            Write-Host 'FAIL'; Write-Host ("      " + $err); $fail = $true
        }
    }
}

Write-Host ''
if (-not $fail) { Write-Host 'Vault databases ready - empty, ready for Module 1.'; exit 0 }
Write-Host 'One or more engines could not be set up. Is the sandbox up (or your own server reachable)? See Demos/Learn/README.md.'
exit 1
