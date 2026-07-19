#!/bin/bash
# filepath: /workspaces/VNTextPatch-wasm/publish-vntextpatch.sh

# Use: ./publish-vntextpatch.sh [plataforma]
# Example: ./publish-vntextpatch.sh linux-x64, or browser-wasm, wasm, wasm32
# If none platform is passed, the default will be "win-x64"

# Desativa telemetria do .NET
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Define plataforma padrão (win-x64) se não for informada
PLATFORM=${1:-win-x64}

echo "📦 Publishing for platform: $PLATFORM"

# Limpeza de diretórios antigos
find . -type d -name bin -exec rm -rf {} +
find . -type d -name obj -exec rm -rf {} +

# Define se será self-contained (exceto para browser-wasm)
SELF_CONTAINED=$( [ "$PLATFORM" != "browser-wasm" ] && echo "--self-contained true" || echo "" )

# Define diretório de saída
OUTPUT_DIR="Build/VNTextPatch-$PLATFORM"

# Publicação
dotnet publish VNTextPatch/VNTextPatch.csproj \
  -c Release \
  -r "$PLATFORM" \
  -o "$OUTPUT_DIR" \
  #-p:RuntimeIdentifier="$PLATFORM" \
  #-p:PublishSingleFile=true
  #-p:PublishAot=true
  $SELF_CONTAINED

# Compacta a pasta de saída em um .zip
#ZIP_FILE="${OUTPUT_DIR}.zip"
#echo "📦 Compactando $OUTPUT_DIR para $ZIP_FILE..."
#cd Build
#zip -r "../$(basename "$ZIP_FILE")" "$(basename "$OUTPUT_DIR")"
#cd ..

#echo "✅ Publicação e compactação concluídas: $ZIP_FILE"
