param([string]$Image = 'urbalink-api:pilot', [string]$DatabaseContainer = 'urbalink-postgres-1')
# PowerShell 7. Builds no images; run docker build -t urbalink-api:pilot . first.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$docker = (Get-Command docker -ErrorAction SilentlyContinue).Source
if (!$docker) { $docker = "$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin\docker.exe" }
$testId = [Guid]::NewGuid().ToString('N')
$database = "urbalink_deploy_test_$testId"
$apiContainer = "urbalink-deploy-test-$testId"
$created = $false
$started = $false
$previousPassword = $env:PGPASSWORD
try {
    $line = Get-Content -LiteralPath (Join-Path $root '.env.local') | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
    if (!$line) { throw 'Falta POSTGRES_PASSWORD.' }
    $env:PGPASSWORD = ($line -split '=', 2)[1]
    $networks = & $docker inspect --format '{{json .NetworkSettings.Networks}}' $DatabaseContainer | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE -ne 0 -or !$networks.Count) { throw 'PostgreSQL necesita una red Docker.' }
    $network = @($networks.Keys)[0]
    & $docker exec $DatabaseContainer createdb -U urbalink $database
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la base de pruebas.' }
    $created = $true
    $environment = @('--network', $network, '-e', "PGHOST=$DatabaseContainer", '-e', 'PGPORT=5432',
        '-e', "PGDATABASE=$database", '-e', 'PGUSER=urbalink', '-e', 'PGPASSWORD',
        '-e', 'Hosting__RailwayEdge=true', '-e', 'RAILWAY_PROJECT_ID=local-deployment-test',
        '-e', 'Database__MigrateOnStartup=false', '-e', 'Logging__LogLevel__Default=Warning')
    & $docker run --rm @environment $Image dotnet UrbaLink.Api.dll --Database:MigrateOnly=true
    if ($LASTEXITCODE -ne 0) { throw 'La migración desde el contenedor falló.' }
    & $docker run --detach --name $apiContainer --publish '127.0.0.1::8080' @environment $Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'El contenedor no arrancó.' }
    $started = $true
    $binding = [string](& $docker port $apiContainer 8080/tcp)
    $url = 'http://' + $binding.Trim()
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try { if ((Invoke-RestMethod "$url/health/ready" -TimeoutSec 1).status -eq 'ready') { $ready = $true; break } }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (!$ready) { throw 'El contenedor no está listo.' }
    $uid = [string](& $docker exec $apiContainer id -u)
    if ($uid.Trim() -eq '0') { throw 'La API está ejecutándose como root.' }
    $headers = @{ 'X-Forwarded-Proto' = 'https'; 'X-Real-IP' = '203.0.113.10' }
    $response = Invoke-WebRequest "$url/api/me" -Headers $headers -SkipHttpErrorCheck
    if ($response.StatusCode -ne 401) { throw 'HTTPS del proxy no se reconoce correctamente.' }
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $redirect = $client.GetAsync("$url/api/me").GetAwaiter().GetResult()
        try { if ([int]$redirect.StatusCode -ne 307 -or $redirect.Headers.Location.Scheme -ne 'https') { throw 'HTTP sin proxy no redirige a HTTPS.' } }
        finally { $redirect.Dispose() }
    } finally { $client.Dispose() }

    $body = @{ Name='Prueba despliegue'; Email="$testId@example.test"; Password='Deployment-test-12345' } | ConvertTo-Json
    $session = Invoke-RestMethod "$url/api/auth/register" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    $authHeaders = $headers.Clone()
    $authHeaders['Authorization'] = 'Bearer ' + $session.token
    if ((Invoke-RestMethod "$url/api/me" -Headers $authHeaders).id -ne $session.account.id) { throw 'La sesión no funciona en producción.' }
    $login = @{ Email="$testId@example.test"; Password='wrong' } | ConvertTo-Json
    $limited = $false
    for ($i = 0; $i -lt 21; $i++) {
        $response = Invoke-WebRequest "$url/api/auth/login" -Method Post -Headers $headers -ContentType 'application/json' -Body $login -SkipHttpErrorCheck
        if ($response.StatusCode -eq 429) { $limited = $true; break }
        if ($response.StatusCode -ne 401) { throw 'Estado inesperado al probar límites.' }
    }
    if (!$limited) { throw 'No se aplica el límite de autenticación.' }
    $headers['X-Real-IP'] = '203.0.113.11'
    $response = Invoke-WebRequest "$url/api/auth/login" -Method Post -Headers $headers -ContentType 'application/json' -Body $login -SkipHttpErrorCheck
    if ($response.StatusCode -ne 401) { throw 'Usuarios con distintas IP comparten el límite del proxy.' }
    & $docker restart $apiContainer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo reiniciar el contenedor de prueba.' }
    # Docker can assign a different ephemeral host port when restarting.
    $binding = [string](& $docker port $apiContainer 8080/tcp)
    $url = 'http://' + $binding.Trim()
    $restored = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try { if ((Invoke-RestMethod "$url/api/me" -Headers $authHeaders -TimeoutSec 1).id -eq $session.account.id) { $restored = $true; break } }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (!$restored) { throw 'No se pudo validar el acceso después de reiniciar el contenedor.' }
    Write-Output 'OK: imagen sin root, migraciones, PG*, readiness, HTTPS del proxy, IP individuales y sesión persistente tras reiniciar.'
} finally {
    # Only remove the unique disposable resources created for this test.
    if ($started -and $apiContainer -match '^urbalink-deploy-test-[a-f0-9]{32}$') { & $docker rm --force $apiContainer | Out-Null }
    if ($created -and $database -match '^urbalink_deploy_test_[a-f0-9]{32}$') { & $docker exec $DatabaseContainer dropdb -U urbalink $database }
    $env:PGPASSWORD = $previousPassword
}
