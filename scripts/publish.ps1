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

$ErrorActionPreference = 'Stop'
# Desativa telemetria do .NET
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
# Define diretorio de saida e caminho do .csproj
$OutputDir  = Join-Path "Build" "VNTextPatch-$Platform"
$CsprojPath = Join-Path "VNTextPatch" "VNTextPatch.csproj"

# Limpeza de diretorios antigos (bin e obj), em qualquer subpasta
$dirsToRemove = Get-ChildItem -Path . -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' }

Write-Host "Cleaning old directories"
Remove-Item $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
foreach ($dir in $dirsToRemove) {
    if (Test-Path $dir.FullName) {
        Remove-Item $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}


# Busca a ultima tag de versao ancestral deste commit
$tag = & git describe --tags --abbrev=0 --match="v[0-9]*" 2>$null
if (-not $tag) {
    $tag = "v1.0.0"
}

# Remove o prefixo "v"
$version = $tag.TrimStart('v')
$version = ($version -split '-', 2)[0]




# Monta os argumentos do dotnet publish (self-contained fica de fora no browser-wasm)
$publishArgs = @(
    'publish'
    $CsprojPath
    '-c', 'Release'
    '-r', $Platform
    '-o', $OutputDir
    '-p:Version=' + $version
    '-p:AssemblyVersion=' + $version + '.0'
    '-p:FileVersion=' + $version + '.0'
    '-p:InformationalVersion=' + $tag
)

if ($Platform -ne 'browser-wasm') {
    $publishArgs += @('--self-contained', 'true')
    $publishArgs += @('-p:PublishSingleFile=true')
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