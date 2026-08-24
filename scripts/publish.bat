@echo off
setlocal
:: Uso: publish.bat [plataforma]
:: Exemplo: publish.bat linux-x64   ou   publish.bat browser-wasm
:: Se nenhuma plataforma for informada, o padrao sera "win-x64"

:: Desativa telemetria do .NET
set DOTNET_CLI_TELEMETRY_OPTOUT=1

:: Define plataforma padrao (win-x64) se nao for informada
set "PLATFORM=%~1"
if "%PLATFORM%"=="" set "PLATFORM=win-x64"
:: Define diretorio de saida
set "OUTPUT_DIR=Build\VNTextPatch-%PLATFORM%"
:: Define se será self-contained (exceto para browser-wasm)
:: SelfContained → controla se o runtime do .NET vai junto.
:: Define se será single-file (exceto para browser-wasm)
set "PUBLISH_ARGS=--self-contained true -p:PublishSingleFile=true"
if "%PLATFORM%"=="browser-wasm" set "PUBLISH_ARGS="


REM Busca a última tag de versão ancestral deste commit
set "TAG="
for /f "delims=" %%i in ('git describe --tags --abbrev=0 --match="v[0-9]*" 2^>nul') do (
    set "TAG=%%i"
)

REM Se não encontrar nenhuma, usa v1.0.0 como fallback
if "%TAG%"=="" set "TAG=v1.0.0"

REM Remove o prefixo "v"
set "VERSION=%TAG%"
if "%VERSION:~0,1%"=="v" set "VERSION=%VERSION:~1%"

REM Remove qualquer sufixo após o primeiro "-"
for /f "delims=-" %%i in ("%VERSION%") do set "VERSION=%%i"




:: Limpeza de diretorios antigos (bin e obj), em qualquer subpasta
echo Cleaning old directories
rd /s /q "%OUTPUT_DIR%" 2>nul
for /d /r %%D in (bin,obj) do (
    if exist "%%D" rd /s /q "%%D" 2>nul
)

echo Publishing for platform: %PLATFORM%

:: Publicacao
:: Flags extras que podem ser adicionadas se precisar:
::   -p:PublishSingleFile=true
::   -p:PublishAot=true
::   --tl:"off" ^
dotnet publish VNTextPatch\VNTextPatch.csproj ^
  -c Release ^
  -r "%PLATFORM%" ^
  -o "%OUTPUT_DIR%" ^
  -p "Version=%VERSION%" ^
  -p "AssemblyVersion=%VERSION%.0" ^
  -p "FileVersion=%VERSION%.0" ^
  -p "InformationalVersion=%TAG%" ^
  %PUBLISH_ARGS%

:: Compacta a pasta de saida em um .zip (opcional - descomente para usar)
:: set "ZIP_FILE=%OUTPUT_DIR%.zip"
:: echo Compactando %OUTPUT_DIR% para %ZIP_FILE%...
:: powershell -NoProfile -Command "Compress-Archive -Path '%OUTPUT_DIR%\*' -DestinationPath '%ZIP_FILE%' -Force"
:: echo Publicacao e compactacao concluidas: %ZIP_FILE%

endlocal