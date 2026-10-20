# Create the Course 8 Module 7 log-hygiene sandbox database on each sandbox
# engine, or on your own server's single activated engine (LEARN_SERVER): ONE EMPTY
# database diag_hygiene. No schema is seeded -- the module deploys a small settings
# package into it. Re-running is safe -- creation is idempotent. PASS is reported only
# after the database is confirmed to exist (a create that silently fails reports FAIL,
# never a false PASS).
#
# -Reset drops and recreates it empty. Use it to start the module's walkthrough over from
# a clean slate after it has broken the database on purpose. Only a database the labs
# created is ever dropped -- see Remove-LabDatabase.
[CmdletBinding()]
param([switch] $Reset)

. "$PSScriptRoot\..\lab-sql.ps1"

$db = 'diag_hygiene'
$labels = @{ sqlserver = 'SQL Server'; postgres = 'PostgreSQL'; mysql = 'MySQL'; mariadb = 'MariaDB' }
$failed = $false
$total = 0

foreach ($engine in Get-LabEngines) {
    Write-Host -NoNewline ("{0,-12} " -f $labels[$engine])
    $lastLabel = $labels[$engine]
    $total++
    try {
        if ($Reset) {
            $removed = Remove-LabDatabase -Engine $engine -Database $db
            if ($removed -eq 'refused') {
                throw "'$db' exists but wasn't created by the labs, so it will not be dropped. Rename or move it, then re-run."
            }
        }
        Confirm-LabDatabase -Engine $engine -Database $db | Out-Null
        Write-Host $(if ($Reset) { 'PASS (reset)' } else { 'PASS (log-hygiene sandbox database ready)' })
    } catch {
        Write-Host 'FAIL'
        Write-Host "    $($_.Exception.Message)"
        $failed = $true
    }
}

Write-Host ''
if (-not $failed) {
    $where = if ($total -eq 1) { $lastLabel } else { "all $total engines" }
    Write-Host "log-hygiene sandbox database ready on $where -- empty, ready for schema deploy."
    exit 0
} else {
    Write-Host 'One or more engines could not be set up. Is the sandbox up (or your own server reachable)? See Demos/Learn/README.md.'
    exit 1
}
