# Create the Course 12 databases on each sandbox engine, or on your own server's
# single activated engine (LEARN_SERVER). Re-running is safe -- all DDL is idempotent.
# PASS is reported only after the database is confirmed to exist (a create that silently
# fails reports FAIL, never a false PASS).
#
# -Reset drops and recreates them empty. Only databases the labs created are ever dropped --
# see Remove-LabDatabase.
[CmdletBinding()]
param([switch] $Reset)

. "$PSScriptRoot\..\lab-sql.ps1"

$failed = $false
$total = 0
$labels = @{ sqlserver = 'SQL Server'; postgres = 'PostgreSQL'; mysql = 'MySQL'; mariadb = 'MariaDB' }

$databases = @(
    'declared_m1'
    'declared_m2'
    'declared_m3'
    'declared_m4'
    'declared_m5'
    'declared_m6'
    'declared_m7'
    'declared_m8'
    'declared_m9'
)

foreach ($engine in Get-LabEngines) {
    Write-Host $labels[$engine]
    foreach ($db in $databases) {
        Write-Host -NoNewline ("  {0,-26} " -f $db)
        $total++
        try {
            if ($Reset) {
                $removed = Remove-LabDatabase -Engine $engine -Database $db
                if ($removed -eq 'refused') {
                    throw "'$db' exists but wasn't created by the labs, so it will not be dropped. Rename or move it, then re-run."
                }
            }
            Confirm-LabDatabase -Engine $engine -Database $db | Out-Null
            Write-Host $(if ($Reset) { 'PASS (reset)' } else { 'PASS' })
        } catch {
            Write-Host 'FAIL'
            Write-Host "    $($_.Exception.Message)"
            $failed = $true
        }
    }
}

Write-Host ''
if (-not $failed) {
    Write-Host "All $total databases are ready."
    exit 0
} else {
    Write-Host 'One or more databases could not be created. Is the sandbox up (or your own server reachable)? See Demos/Learn/README.md.'
    exit 1
}
