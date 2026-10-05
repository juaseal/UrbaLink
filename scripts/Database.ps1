param([ValidateSet('Start', 'Status', 'Stop')][string]$Action = 'Start')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$previousPath = $env:PATH
try {
    if (!(Get-Command docker -ErrorAction SilentlyContinue)) {
        foreach ($directory in @(
            "$env:LOCALAPPDATA\Programs\DockerDesktop\resources\bin",
            "$env:ProgramFiles\Docker\Docker\resources\bin"
        )) {
            if (Test-Path (Join-Path $directory 'docker.exe')) {
                $env:PATH = "$directory;$env:PATH"
                break
            }
        }
    }
    if (!(Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'No se encuentra Docker Desktop. Instálalo y ábrelo primero.'
    }
    $envFile = Join-Path $root '.env.local'
    if (!(Test-Path -LiteralPath $envFile -PathType Leaf)) {
        $envFile = Join-Path $root '.env'
    }
    if (!(Test-Path -LiteralPath $envFile -PathType Leaf)) {
        throw 'Copia .env.example a .env.local y configura POSTGRES_PASSWORD.'
    }
    $arguments = @('compose', '--project-directory', $root, '--env-file', $envFile, '-f', (Join-Path $root 'compose.yaml'))
    switch ($Action) {
        'Start' { $arguments += @('up', '-d', '--wait', '--wait-timeout', '90') }
        'Status' { $arguments += 'ps' }
        'Stop' { $arguments += 'stop' }
    }
    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Docker no pudo completar la operación. Comprueba que Docker Desktop esté abierto.' }
} finally {
    $env:PATH = $previousPath
}
