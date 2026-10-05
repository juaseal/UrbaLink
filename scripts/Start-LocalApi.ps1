param([switch]$Lan)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/UrbaLink.Api/UrbaLink.Api.csproj'
$profile = if ($Lan) { 'lan' } else { 'local' }
dotnet run --project $project --launch-profile $profile
exit $LASTEXITCODE
