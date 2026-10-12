param([ValidateSet('posts', 'comments', 'lifecycle', 'market', 'rides', 'services', 'architecture', 'accounts')][string]$Suite = 'posts')
# Requires PowerShell 7, Docker Desktop and the project's local PostgreSQL container.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$previousPath = $env:PATH
$previousTestConnection = $env:URBALINK_TEST_CONNECTION
$dbName = 'urbalink_posts_test_' + [Guid]::NewGuid().ToString('N')
$created = $false
$process = $null
Push-Location $root
try {
    foreach ($directory in @("$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin", "$env:ProgramFiles\Docker\Docker\resources\bin")) {
        if (Test-Path (Join-Path $directory 'docker.exe')) { $env:PATH = "$directory;$env:PATH"; break }
    }
    $envFile = if (Test-Path .env.local -PathType Leaf) { '.env.local' } else { '.env' }
    $line = Get-Content -LiteralPath $envFile | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
    if (!$line) { throw 'Falta POSTGRES_PASSWORD.' }
    $password = ($line -split '=',2)[1]
    $container = [string](& docker compose --env-file $envFile ps -q postgres)
    $container = ([string]$container).Trim()
    if ($LASTEXITCODE -ne 0 -or !$container) { throw 'Arranca PostgreSQL antes de ejecutar las pruebas.' }
    $artifacts = Join-Path $root '.artifacts/posts-tests'
    dotnet build src/UrbaLink.Api --artifacts-path $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de API.' }
    dotnet build tests/UrbaLink.Checks --artifacts-path $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación de pruebas.' }

    & docker exec $container psql -U urbalink -d postgres -c "CREATE DATABASE $dbName" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la base temporal.' }
    $created = $true
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $url = "http://127.0.0.1:$port"
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection['Host'] = '127.0.0.1'
    $connection['Database'] = $dbName
    $connection['Username'] = 'urbalink'
    $connection['Password'] = $password
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add((Join-Path $artifacts 'bin/UrbaLink.Api/debug/UrbaLink.Api.dll'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Join-Path $root 'src/UrbaLink.Api'
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $start.Environment['ASPNETCORE_URLS'] = $url
    $start.Environment['ConnectionStrings__UrbaLink'] = $connection.ConnectionString
    $start.Environment['Logging__LogLevel__Default'] = 'Warning'
    $start.Environment['Availability__MaxConcurrentRequests'] = if ($Suite -eq 'architecture') { '4' } else { '64' }
    $checks = Join-Path $artifacts 'bin/UrbaLink.Checks/debug/UrbaLink.Checks.dll'
    if ($Suite -eq 'architecture') {
        $start.ArgumentList.Add('--Database:MigrateOnly=true')
        $migrationProcess = [System.Diagnostics.Process]::Start($start)
        try {
            if (!$migrationProcess.WaitForExit(30000)) { $migrationProcess.Kill(); throw 'La migración independiente no terminó.' }
            if ($migrationProcess.ExitCode -ne 0) { throw 'Falló la migración independiente.' }
        } finally { $migrationProcess.Dispose() }
        $start.ArgumentList.RemoveAt(1)
        $start.Environment['Database__MigrateOnStartup'] = 'false'
    }
    for ($run = 0; $run -lt 2; $run++) {
        $process = [System.Diagnostics.Process]::Start($start)
        $ready = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            if ($process.HasExited) { throw 'La API no pudo arrancar.' }
            try {
                $health = Invoke-RestMethod "$url/health/ready" -TimeoutSec 1
                if ($health.status -eq 'ready') { $ready = $true; break }
            } catch { Start-Sleep -Milliseconds 250 }
        }
        if (!$ready) { throw 'La API no quedó lista.' }
        if ($run -eq 0) {
            if ($Suite -in @('market', 'architecture', 'accounts')) { $env:URBALINK_TEST_CONNECTION = $connection.ConnectionString }
            & dotnet $checks $url $Suite
            if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas del tablón.' }
            $countBefore = & docker exec $container psql -U urbalink -d $dbName -Atc 'SELECT (SELECT count(*) FROM users) + (SELECT count(*) FROM posts) + (SELECT count(*) FROM comments) + (SELECT count(*) FROM market_items) + (SELECT count(*) FROM market_bids) + (SELECT count(*) FROM market_messages)'
        } else {
            $countAfter = & docker exec $container psql -U urbalink -d $dbName -Atc 'SELECT (SELECT count(*) FROM users) + (SELECT count(*) FROM posts) + (SELECT count(*) FROM comments) + (SELECT count(*) FROM market_items) + (SELECT count(*) FROM market_bids) + (SELECT count(*) FROM market_messages)'
            if ($countBefore -ne $countAfter -or [int]$countAfter -lt 1) { throw 'Se perdieron publicaciones al reiniciar.' }
            $versions = & docker exec $container psql -U urbalink -d $dbName -Atc 'SELECT count(*) FROM schema_versions'
            if ([int]$versions -ne @(Get-ChildItem (Join-Path $root 'src/UrbaLink.Api/Data') -Filter '*.sql').Count) { throw 'Migraciones inesperadas.' }
            Write-Output 'OK: persistencia y migraciones idempotentes tras reiniciar.'
        }
        $process.Kill()
        $process.WaitForExit()
        $process.Dispose()
        $process = $null
    }
} finally {
    if ($process) {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    if ($created -and $dbName -match '^urbalink_posts_test_[a-f0-9]{32}$') {
        & docker exec $container psql -U urbalink -d postgres -c "DROP DATABASE $dbName" | Out-Null
    }
    $env:URBALINK_TEST_CONNECTION = $previousTestConnection
    $env:PATH = $previousPath
    Pop-Location
}


