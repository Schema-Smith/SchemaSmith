# Shared connection helper for the Learn labs.
#
# Sandbox (default): runs each engine's client inside its container, exactly as the labs
# always have. Own server (LEARN_SERVER set by use-my-server.ps1): runs the engine's
# client on your machine against your endpoint. The output is identical either way, so
# callers never care which path ran.
#
# Dot-source it as a library:  . ..\lab-sql.ps1
# Or call it as a command:     ..\lab-sql.ps1 sqlserver ordersservice_prod "SELECT 1"

function Test-LabOwnServer { return -not [string]::IsNullOrWhiteSpace($env:LEARN_SERVER) }

function Get-LabEngines {
    if (-not (Test-LabOwnServer)) { return @('sqlserver', 'postgres', 'mysql', 'mariadb') }
    if ([string]::IsNullOrWhiteSpace($env:LEARN_ENGINE)) {
        throw 'LAB-SQL: LEARN_SERVER is set but LEARN_ENGINE is not. Re-run use-my-server.ps1.'
    }
    return @($env:LEARN_ENGINE)
}

function Get-LabEndpointLabel {
    param([Parameter(Mandatory)][string]$Engine)
    if (Test-LabOwnServer) { return "$($env:LEARN_SERVER):$($env:LEARN_PORT)" }
    return "learn-$Engine (container)"
}

function Get-LabClient {
    param([Parameter(Mandatory)][string]$Engine)
    # MariaDB renamed its client to 'mariadb' in later releases; 10.2-10.4 ship only 'mysql'.
    # Accept either so a learner on a supported-but-older MariaDB isn't turned away.
    $candidates = @{ sqlserver = @('sqlcmd'); postgres = @('psql'); mysql = @('mysql'); mariadb = @('mariadb', 'mysql') }[$Engine]
    foreach ($c in $candidates) {
        if (Get-Command $c -ErrorAction SilentlyContinue) { return $c }
    }
    $shown = $candidates -join "' or '"
    throw "LAB-SQL: '$shown' is required to reach your own $Engine server but is not on PATH. Install the engine's command-line client (see docs/end-user/guide/use-your-own-server.md), re-open your shell, and verify with '$($candidates[0]) --version'."
}

# The -S value for sqlcmd. A named instance ("localhost\SQLEXPRESS") carries its own routing and must
# NOT have a port appended -- and Windows Authentication requires that form anyway, since there is no
# SPN to resolve for a loopback address and port.
function Get-LabSqlTarget {
    $server = $env:LEARN_SERVER
    $port   = $env:LEARN_PORT
    if ($server -like '*\*' -or [string]::IsNullOrEmpty($port)) { return $server }
    return "$server,$port"
}

# sqlcmd's auth arguments. Windows Authentication when use-my-server left no credential.
#
# The comma is load-bearing: PowerShell unwraps a single-element array on return, so `@('-E')` comes
# back as the bare string '-E' and splatting it passes each CHARACTER as an argument -- sqlcmd then
# reports "'': Unknown Option". `,'-E'` forces it to stay an array.
function Get-LabSqlAuth {
    if ([string]::IsNullOrEmpty($env:LEARN_USER) -and [string]::IsNullOrEmpty($env:LEARN_PASSWORD)) {
        return ,'-E'
    }
    return @('-U', $env:LEARN_USER, '-P', $env:LEARN_PASSWORD)
}

function Get-LabContainer {
    param([Parameter(Mandatory)][string]$Engine)
    return @{ sqlserver = 'learn-sqlserver'; postgres = 'learn-postgres'; mysql = 'learn-mysql'; mariadb = 'learn-mariadb' }[$Engine]
}

# MariaDB 10.2-10.4 containers have no 'mariadb' binary -- only 'mysql'. Probe once per
# container and cache, so a floor-version sandbox is reachable without a per-call check.
$script:LabMariaClientCache = @{}
function Get-LabContainerMariaClient {
    param([Parameter(Mandatory)][string]$Container)
    if ($script:LabMariaClientCache.ContainsKey($Container)) { return $script:LabMariaClientCache[$Container] }
    docker exec $Container sh -c 'command -v mariadb >/dev/null 2>&1' 2>&1 | Out-Null
    $client = if ($LASTEXITCODE -eq 0) { 'mariadb' } else { 'mysql' }
    $script:LabMariaClientCache[$Container] = $client
    return $client
}

# The SQL Server 2017 image ships ONLY /opt/mssql-tools; 2019+ ship /opt/mssql-tools18. Probe once per
# container and cache, exactly as the MariaDB client is handled -- see the Bash twin for why the wrong
# path looks identical to a server that never came up.
$script:LabSqlcmdCache = @{}
function Get-LabContainerSqlcmd {
    param([Parameter(Mandatory)][string]$Container)
    if ($script:LabSqlcmdCache.ContainsKey($Container)) { return $script:LabSqlcmdCache[$Container] }
    docker exec $Container sh -c 'test -x /opt/mssql-tools18/bin/sqlcmd' 2>&1 | Out-Null
    $sqlcmd = if ($LASTEXITCODE -eq 0) { '/opt/mssql-tools18/bin/sqlcmd' } else { '/opt/mssql-tools/bin/sqlcmd' }
    $script:LabSqlcmdCache[$Container] = $sqlcmd
    return $sqlcmd
}

# PowerShell 5.1 wraps a native command's redirected stderr in ErrorRecords, which
# $ErrorActionPreference='Stop' (set by most lab scripts) turns into a terminating error
# before we can inspect the exit code. Reading them as plain text keeps our own message
# the one the learner sees.
#
# Keep the two streams APART. Clients chatter on stderr even when they succeed -- MariaDB
# warns about "insecure passwordless login" on every connection -- and folding that into
# the result makes a query for a count return "WARNING...\n0" instead of "0". Callers
# compare results against exact values, so contaminated output silently takes the wrong
# branch. That is the same failure this helper exists to prevent.
function Split-LabStreams($Streams) {
    $text = { param($s) (($s | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { $_ }
    }) -join [Environment]::NewLine).Trim() }
    return @{
        Output = & $text @($Streams | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
        Error  = & $text @($Streams | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
    }
}

function Invoke-LabSql {
    param(
        [Parameter(Mandatory)][string]$Engine,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$Sql
    )
    $ErrorActionPreference = 'Continue'
    $out = $null
    if (Test-LabOwnServer) {
        $client = Get-LabClient $Engine
        $server = $env:LEARN_SERVER; $port = $env:LEARN_PORT
        $user = $env:LEARN_USER; $password = $env:LEARN_PASSWORD
        switch ($Engine) {
            'sqlserver' { $out = & sqlcmd -S (Get-LabSqlTarget) @(Get-LabSqlAuth) -C -b -d $Database -h -1 -W -Q "SET NOCOUNT ON; $Sql" 2>&1 }
            'postgres'  { $env:PGPASSWORD = $password
                          $out = & psql -h $server -p $port -U $user -d $Database -w -v ON_ERROR_STOP=1 --no-psqlrc -tAc $Sql 2>&1 }
            default     { $env:MYSQL_PWD = $password
                          $out = & $client -h $server -P $port -u $user -N -s -D $Database -e $Sql 2>&1 }
        }
    }
    else {
        $container = Get-LabContainer $Engine
        switch ($Engine) {
            'sqlserver' { $sc = Get-LabContainerSqlcmd $container; $out = docker exec $container $sc -S localhost -U sa -P 'Learn!Passw0rd' -C -b -d $Database -h -1 -W -Q "SET NOCOUNT ON; $Sql" 2>&1 }
            'postgres'  { $out = docker exec $container psql -U postgres -d $Database -v ON_ERROR_STOP=1 -tAc $Sql 2>&1 }
            'mysql'     { $out = docker exec -e MYSQL_PWD=Learn!Passw0rd $container mysql -uroot -N -s -D $Database -e $Sql 2>&1 }
            'mariadb'   { $mc = Get-LabContainerMariaClient -Container $container; $out = docker exec -e MYSQL_PWD=Learn!Passw0rd $container $mc -uroot -N -s -D $Database -e $Sql 2>&1 }
        }
    }
    $streams = Split-LabStreams $out
    if ($LASTEXITCODE -ne 0) {
        $detail = if ($streams.Error) { $streams.Error } else { $streams.Output }
        throw "LAB-SQL: could not reach $Engine at $(Get-LabEndpointLabel $Engine) [database '$Database'] -- $detail"
    }
    return $streams.Output
}

function Invoke-LabSqlFile {
    param(
        [Parameter(Mandatory)][string]$Engine,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][string]$Path
    )
    if (-not (Test-Path $Path)) { throw "LAB-SQL: no such SQL file '$Path'." }
    $ErrorActionPreference = 'Continue'
    $out = $null
    if (Test-LabOwnServer) {
        # Local clients read the file directly, so PowerShell 5.1's BOM-on-pipe problem
        # never arises and nothing has to be copied into a container.
        $client = Get-LabClient $Engine
        $server = $env:LEARN_SERVER; $port = $env:LEARN_PORT
        $user = $env:LEARN_USER; $password = $env:LEARN_PASSWORD
        switch ($Engine) {
            'sqlserver' { $out = & sqlcmd -S (Get-LabSqlTarget) @(Get-LabSqlAuth) -C -b -d $Database -i $Path 2>&1 }
            'postgres'  { $env:PGPASSWORD = $password
                          $out = & psql -h $server -p $port -U $user -d $Database -w -v ON_ERROR_STOP=1 --no-psqlrc -f $Path 2>&1 }
            default     { $env:MYSQL_PWD = $password
                          $out = & $client -h $server -P $port -u $user -D $Database -e "source $Path" 2>&1 }
        }
    }
    else {
        $container = Get-LabContainer $Engine
        docker cp $Path "${container}:/tmp/lab-seed.sql" 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "LAB-SQL: could not stage '$([System.IO.Path]::GetFileName($Path))' into $container."
        }
        switch ($Engine) {
            'sqlserver' { $sc = Get-LabContainerSqlcmd $container; $out = docker exec $container $sc -S localhost -U sa -P 'Learn!Passw0rd' -C -b -d $Database -i /tmp/lab-seed.sql 2>&1 }
            'postgres'  { $out = docker exec $container psql -U postgres -d $Database -v ON_ERROR_STOP=1 -f /tmp/lab-seed.sql 2>&1 }
            'mysql'     { $out = docker exec -e MYSQL_PWD=Learn!Passw0rd $container mysql -uroot -D $Database -e 'source /tmp/lab-seed.sql' 2>&1 }
            'mariadb'   { $mc = Get-LabContainerMariaClient -Container $container; $out = docker exec -e MYSQL_PWD=Learn!Passw0rd $container $mc -uroot -D $Database -e 'source /tmp/lab-seed.sql' 2>&1 }
        }
    }
    if ($LASTEXITCODE -ne 0) {
        $streams = Split-LabStreams $out
        $detail = if ($streams.Error) { $streams.Error } else { $streams.Output }
        throw "LAB-SQL: failed running '$([System.IO.Path]::GetFileName($Path))' against $Engine at $(Get-LabEndpointLabel $Engine) [database '$Database'] -- $detail"
    }
}

$LabStamp = 'SchemaSmith_DemoProvisioned'

function Add-LabStamp {
    param([Parameter(Mandatory)][string]$Engine, [Parameter(Mandatory)][string]$Database)
    switch ($Engine) {
        'sqlserver' { Invoke-LabSql -Engine $Engine -Database $Database -Sql "IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 0 AND name = '$LabStamp') EXEC sys.sp_addextendedproperty @name = N'$LabStamp', @value = N'1'" | Out-Null }
        'postgres'  { Invoke-LabSql -Engine $Engine -Database $Database -Sql "COMMENT ON DATABASE ""$Database"" IS '$LabStamp'" | Out-Null }
        default     { Invoke-LabSql -Engine $Engine -Database $Database -Sql "CREATE TABLE IF NOT EXISTS ``$LabStamp`` (marker TINYINT NOT NULL)" | Out-Null }
    }
}

function Get-LabAdminDatabase {
    param([Parameter(Mandatory)][string]$Engine)
    return @{ sqlserver = 'master'; postgres = 'postgres'; mysql = 'information_schema'; mariadb = 'information_schema' }[$Engine]
}

# Creates the database if it's absent and stamps it as lab-provisioned. An existing
# database that carries the stamp is reused. An existing database WITHOUT the stamp is
# adopted in the sandbox (the container is a throwaway we created, and sandboxes built
# before this helper carry no stamp) but REFUSED on your own server, where it is far
# more likely to be a real database of yours that a deploy would damage.
function Confirm-LabDatabase {
    param([Parameter(Mandatory)][string]$Engine, [Parameter(Mandatory)][string]$Database)
    $admin = Get-LabAdminDatabase $Engine

    $existsSql = switch ($Engine) {
        'sqlserver' { "SELECT COUNT(*) FROM sys.databases WHERE name = '$Database'" }
        'postgres'  { "SELECT COUNT(*) FROM pg_database WHERE datname = '$Database'" }
        default     { "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '$Database'" }
    }
    if ((Invoke-LabSql -Engine $Engine -Database $admin -Sql $existsSql) -eq '0') {
        # Take the server's default character set and collation. Forcing one here (the demo
        # helper pins utf8mb4_unicode_ci) leaves lab databases collated differently from
        # everything else on the server -- MySQL 8 defaults to utf8mb4_0900_ai_ci and MariaDB
        # 11.4 to utf8mb4_uca1400_ai_ci -- and any later comparison across the two fails with
        # "Illegal mix of collations". The labs have always used the server default.
        $createSql = switch ($Engine) {
            'sqlserver' { "CREATE DATABASE [$Database]" }
            'postgres'  { "CREATE DATABASE ""$Database""" }
            default     { "CREATE DATABASE ``$Database``" }
        }
        Invoke-LabSql -Engine $Engine -Database $admin -Sql $createSql | Out-Null
        Add-LabStamp -Engine $Engine -Database $Database
        return 'created'
    }

    $stampedSql = switch ($Engine) {
        'sqlserver' { "SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = '$LabStamp'" }
        'postgres'  { "SELECT COUNT(*) FROM pg_database WHERE datname = current_database() AND COALESCE(shobj_description(oid, 'pg_database'), '') = '$LabStamp'" }
        default     { "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$Database' AND table_name = '$LabStamp'" }
    }
    if ((Invoke-LabSql -Engine $Engine -Database $Database -Sql $stampedSql) -ne '0') { return 'reused' }

    if (Test-LabOwnServer) {
        throw @"
LAB-SQL: a database named '$Database' already exists on $(Get-LabEndpointLabel $Engine) but was NOT created by the labs.
The labs will not touch a database they didn't create -- deploying into it could drop columns.
Rename or move your '$Database', then re-run this setup script.
"@
    }
    Add-LabStamp -Engine $Engine -Database $Database
    return 'reused'
}

# Drops a database so a course can start from a clean slate. Mirrors Confirm-LabDatabase's
# ownership rule: on your own server only a database the labs stamped is ever dropped, so a
# real database of yours that shares a lab name is refused, never destroyed. In the sandbox
# the container is a throwaway we created, so an unstamped database is dropped too.
# Returns 'dropped', 'absent', or 'refused'.
function Remove-LabDatabase {
    param([Parameter(Mandatory)][string]$Engine, [Parameter(Mandatory)][string]$Database)
    $admin = Get-LabAdminDatabase $Engine

    $existsSql = switch ($Engine) {
        'sqlserver' { "SELECT COUNT(*) FROM sys.databases WHERE name = '$Database'" }
        'postgres'  { "SELECT COUNT(*) FROM pg_database WHERE datname = '$Database'" }
        default     { "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '$Database'" }
    }
    if ((Invoke-LabSql -Engine $Engine -Database $admin -Sql $existsSql) -eq '0') { return 'absent' }

    if (Test-LabOwnServer) {
        $stampedSql = switch ($Engine) {
            'sqlserver' { "SELECT COUNT(*) FROM sys.extended_properties WHERE class = 0 AND name = '$LabStamp'" }
            'postgres'  { "SELECT COUNT(*) FROM pg_database WHERE datname = current_database() AND COALESCE(shobj_description(oid, 'pg_database'), '') = '$LabStamp'" }
            default     { "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$Database' AND table_name = '$LabStamp'" }
        }
        if ((Invoke-LabSql -Engine $Engine -Database $Database -Sql $stampedSql) -eq '0') { return 'refused' }
    }

    # PostgreSQL: evict sessions and drop as SEPARATE statements. psql -c wraps its whole
    # string in one transaction, and DROP DATABASE cannot run inside a transaction block.
    if ($Engine -eq 'postgres') {
        Invoke-LabSql -Engine $Engine -Database $admin `
            -Sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid()" | Out-Null
        Invoke-LabSql -Engine $Engine -Database $admin -Sql "DROP DATABASE IF EXISTS ""$Database""" | Out-Null
        return 'dropped'
    }

    $dropSql = switch ($Engine) {
        'sqlserver' { "ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database];" }
        default     { "DROP DATABASE IF EXISTS ``$Database``" }
    }
    Invoke-LabSql -Engine $Engine -Database $admin -Sql $dropSql | Out-Null
    return 'dropped'
}

# Called as a command rather than dot-sourced:
#   lab-sql.ps1 <engine> <database> "<sql>"
#   lab-sql.ps1 <engine> <database> --file <path.sql>
if ($MyInvocation.InvocationName -ne '.' -and $args.Count -ge 3) {
    if ($args[2] -eq '--file') {
        if ($args.Count -lt 4) { throw 'LAB-SQL: --file needs a path. Usage: lab-sql.ps1 <engine> <database> --file <path.sql>' }
        Invoke-LabSqlFile -Engine $args[0] -Database $args[1] -Path $args[3]
    }
    else {
        Invoke-LabSql -Engine $args[0] -Database $args[1] -Sql ($args[2..($args.Count - 1)] -join ' ')
    }
}
