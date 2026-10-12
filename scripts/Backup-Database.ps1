param([string]$Container = 'urbalink-postgres-1')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$docker = (Get-Command docker -ErrorAction SilentlyContinue).Source
if (!$docker) {
    foreach ($candidate in @("$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin\docker.exe", "$env:ProgramFiles\Docker\Docker\resources\bin\docker.exe")) {
        if (Test-Path -LiteralPath $candidate) { $docker = $candidate; break }
    }
}
if (!$docker) { throw 'No se encuentra Docker Desktop.' }
$backupId = [Guid]::NewGuid().ToString('N')
$remote = "/tmp/urbalink_backup_$backupId.dump"
$directory = Join-Path $root '.artifacts/backups'
$null = New-Item -ItemType Directory -Force -Path $directory
$destination = Join-Path $directory ("urbalink_{0}_{1}.dump" -f [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss'), $backupId)
try {
    # PostgreSQL produces a consistent custom-format archive. Avoid binary shell pipelines.
    & $docker exec $Container pg_dump -U urbalink -d urbalink --format=custom --file=$remote
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la copia de PostgreSQL.' }
    & $docker exec $Container pg_restore --list $remote | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La copia no contiene un archivo válido de PostgreSQL.' }
    & $docker cp "${Container}:$remote" $destination
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $destination)) { throw 'No se pudo copiar el archivo al equipo.' }
    Write-Output "Copia local: $destination"
    Write-Output 'No sustituye una copia externa ni una prueba de restauración.'
} finally {
    # Only remove this invocation's temporary dump; never database directories or volumes.
    if ($remote -match '^/tmp/urbalink_backup_[a-f0-9]{32}\.dump$') {
        & $docker exec $Container rm -- $remote | Out-Null
    }
}
