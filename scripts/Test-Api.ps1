$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $artifacts = Join-Path $root '.artifacts/availability-tests'
    dotnet build src/UrbaLink.Api --artifacts-path $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar la API.' }
    dotnet build tests/UrbaLink.Checks --artifacts-path $artifacts
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron compilar las comprobaciones.' }

    foreach ($scenario in @('database_not_configured', 'database_unavailable')) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $url = "http://127.0.0.1:$port"
        $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.ArgumentList.Add((Join-Path $artifacts 'bin/UrbaLink.Api/debug/UrbaLink.Api.dll'))
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WorkingDirectory = Join-Path $root 'src/UrbaLink.Api'
        $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
        $start.Environment['ASPNETCORE_URLS'] = $url
        $start.Environment['Database__MigrateOnStartup'] = 'false'
        $start.Environment['ConnectionStrings__UrbaLink'] = if ($scenario -eq 'database_not_configured') {
            ''
        } else {
            'Host=127.0.0.1;Port=1;Database=urbalink;Username=test;Password=test;Timeout=1'
        }
        $process = [System.Diagnostics.Process]::Start($start)
        try {
            $started = $false
            for ($attempt = 0; $attempt -lt 40; $attempt++) {
                if ($process.HasExited) { throw 'La API se cerró durante el arranque.' }
                try {
                    $null = Invoke-RestMethod "$url/health/live" -TimeoutSec 1
                    $started = $true
                    break
                } catch { Start-Sleep -Milliseconds 250 }
            }
            if (!$started) { throw 'La API no arrancó a tiempo.' }
            dotnet (Join-Path $artifacts 'bin/UrbaLink.Checks/debug/UrbaLink.Checks.dll') $url $scenario
            if ($LASTEXITCODE -ne 0) { throw "Falló $scenario" }
        } finally {
            if (!$process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
            $process.Dispose()
        }
    }
} finally {
    Pop-Location
}
