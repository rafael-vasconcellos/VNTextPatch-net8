<#
.SYNOPSIS
    Publica o VNTextPatch para a plataforma especificada.
.PARAMETER Platform
    Plataforma de destino (ex: win-x64, linux-x64, browser-wasm). Padrao: win-x64.
.EXAMPLE
    .\publish-vntextpatch.ps1 linux-x64
    .\publish-vntextpatch.ps1 -Platform browser-wasm
#>
param(
    [string]$Platform = "win-x64"
)

# Desativa telemetria do .NET
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

# Limpeza de diretorios antigos (bin e obj), em qualquer subpasta
$dirsToRemove = Get-ChildItem -Path . -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' }

Write-Host "Cleaning old directories"
foreach ($dir in $dirsToRemove) {
    if (Test-Path $dir.FullName) {
        Remove-Item $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Define diretorio de saida e caminho do .csproj
$OutputDir  = Join-Path "Build" "VNTextPatch-$Platform"
$CsprojPath = Join-Path "VNTextPatch" "VNTextPatch.csproj"

# Monta os argumentos do dotnet publish (self-contained fica de fora no browser-wasm)
$publishArgs = @(
    'publish'
    $CsprojPath
    '-c', 'Release'
    '-r', $Platform
    '-o', $OutputDir
)

if ($Platform -ne 'browser-wasm') {
    $publishArgs += @('--self-contained', 'true')
}

# Flags extras que podem ser adicionadas se precisar:
# $publishArgs += '-p:PublishSingleFile=true'
# $publishArgs += '-p:PublishAot=true'

Write-Host "Publishing for platform: $Platform"
dotnet @publishArgs

# Compacta a pasta de saida em um .zip (opcional - descomente para usar)
# $zipFile = "$OutputDir.zip"
# Write-Host "Compactando $OutputDir para $zipFile..."
# Compress-Archive -Path "$OutputDir\*" -DestinationPath $zipFile -Force
# Write-Host "Publicacao e compactacao concluidas: $zipFile"