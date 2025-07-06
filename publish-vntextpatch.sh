#!/bin/bash
# filepath: /workspaces/VNTextPatch-wasm/publish-vntextpatch.sh

# Use: ./publish-vntextpatch.sh [plataforma]
# Example: ./publish-vntextpatch.sh linux-x64
# If none platform is passed, the default will be "win-x64"

# Desativa telemetria do .NET
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Define plataforma padrão (win-x64) se não for informada
PLATFORM=${1:-win-x64}

echo "📦 Publishing for platform: $PLATFORM"

# Limpeza de diretórios antigos
find . -type d -name bin -exec rm -rf {} +
find . -type d -name obj -exec rm -rf {} +

dotnet publish VNTextPatch/VNTextPatch.csproj \
  -c Release \
  -r "$PLATFORM" \
  --self-contained true \
  -o "Build/VNTextPatch-$PLATFORM"
