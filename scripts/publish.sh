#!/bin/bash
set -euo pipefail
# Use: ./publish.sh [plataforma]
# Example: ./publish.sh linux-x64, or browser-wasm, wasm, wasm32
# If none platform is passed, the default will be "win-x64"

# Desativa telemetria do .NET
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# Define plataforma padrão (win-x64) se não for informada
PLATFORM=${1:-win-x64}
# Define diretório de saída
OUTPUT_DIR="Build/VNTextPatch-$PLATFORM"
echo "📦 Publishing for platform: $PLATFORM"

# Limpeza de diretórios antigos
find . -type d -name bin -exec rm -rf {} +
find . -type d -name obj -exec rm -rf {} +
rm -rf "$OUTPUT_DIR"

# Define se será self-contained (exceto para browser-wasm)
# SelfContained → controla se o runtime do .NET vai junto.
# Define se será single-file (exceto para browser-wasm)
PUBLISH_ARGS=()
if [ "$PLATFORM" != "browser-wasm" ]; then
    PUBLISH_ARGS+=(--self-contained true)
    PUBLISH_ARGS+=("-p:PublishSingleFile=true")
fi

# Busca a última tag de versão ancestral deste commit.
TAG=$(git describe --tags --abbrev=0 --match='v[0-9]*' 2>/dev/null || true)

# Se não encontrar nenhuma, usa v1.0.0 como fallback.
if [ -z "$TAG" ]; then
    TAG="v1.0.0"
fi

# Remove o prefixo "v" para as propriedades do .NET
VERSION=${TAG#v}
# Remove qualquer sufixo após o primeiro "-".
VERSION="${VERSION%%-*}"




# Publicação
dotnet publish VNTextPatch/VNTextPatch.csproj \
  -c Release \
  -r "$PLATFORM" \
  -o "$OUTPUT_DIR" \
  -p:Version="$VERSION" \
  -p:AssemblyVersion="${VERSION}.0" \
  -p:FileVersion="${VERSION}.0" \
  -p:InformationalVersion="${TAG#v}" \
  "${PUBLISH_ARGS[@]}"

  #-p:RuntimeIdentifier="$PLATFORM" \
  #-p:PublishSingleFile=true \
  #-p:PublishAot=true \

# Compacta a pasta de saída em um .zip
#ZIP_FILE="${OUTPUT_DIR}.zip"
#echo "📦 Compactando $OUTPUT_DIR para $ZIP_FILE..."
#cd Build
#zip -r "../$(basename "$ZIP_FILE")" "$(basename "$OUTPUT_DIR")"
#cd ..

#echo "✅ Publicação e compactação concluídas: $ZIP_FILE"
