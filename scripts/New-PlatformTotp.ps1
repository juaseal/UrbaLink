# Generates a NEW secret locally. Run yourself; never commit its output.
param([Parameter(Mandatory)][Guid]$UserId)
$ErrorActionPreference='Stop'
$secretBytes=[System.Security.Cryptography.RandomNumberGenerator]::GetBytes(20)
$alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
$secretBuilder=[System.Text.StringBuilder]::new()
$totpBits=0; $totpValue=0
foreach($byte in $secretBytes) {
    for($bit=7;$bit -ge 0;$bit--) {
        $totpValue=($totpValue -shl 1) -bor (($byte -shr $bit) -band 1)
        $totpBits++
        if($totpBits -eq 5){[void]$secretBuilder.Append($alphabet[$totpValue]);$totpBits=0;$totpValue=0}
    }
}
$secret=$secretBuilder.ToString()
Write-Host 'Guarda esta clave en tu autenticador como TOTP: 6 dígitos, SHA-1, periodo 30 segundos.'
Write-Host 'No compartas la clave ni la guardes en Git.'
Write-Host "Variable de Railway: PlatformAdmin__TotpSecrets__$($UserId.ToString('N'))"
Write-Host "Valor: $secret"
Write-Host "PlatformAdmin__UserIds debe incluir $($UserId.ToString())."

