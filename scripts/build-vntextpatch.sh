#!/bin/bash
# filepath: /workspaces/VNTextPatch-wasm/build-vntextpatch.sh

export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Limpeza dos diretórios antigos
find . -type d -name bin -exec rm -rf {} +
find . -type d -name obj -exec rm -rf {} +
rm -rf Build/VNTextPatch
rm -rf Debug
rm -rf Release

mkdir -p Build

# Restaura dependências
dotnet restore VNTextPatch/VNTextPatch.csproj



PLATFORM=${1:-linux-x64}
# Compila em Release para a pasta Build/VNTextPatch
dotnet build VNTextPatch/VNTextPatch.csproj \
    -c Release \
    -o Build/VNTextPatch \
    -p:RuntimeIdentifier="$PLATFORM" \

# Remove arquivos indesejados
rm -f Build/VNTextPatch/*.pdb
rm -f Build/VNTextPatch/*.txt