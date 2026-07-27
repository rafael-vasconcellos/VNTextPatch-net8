@echo off
setlocal

:: Uso: publish-vntextpatch.bat [plataforma]
:: Exemplo: publish-vntextpatch.bat linux-x64   ou   publish-vntextpatch.bat browser-wasm
:: Se nenhuma plataforma for informada, o padrao sera "win-x64"

:: Desativa telemetria do .NET
set DOTNET_CLI_TELEMETRY_OPTOUT=1

:: Define plataforma padrao (win-x64) se nao for informada
set "PLATFORM=%~1"
if "%PLATFORM%"=="" set "PLATFORM=win-x64"

:: Limpeza de diretorios antigos (bin e obj), em qualquer subpasta
echo Cleaning old directories
for /d /r %%D in (bin,obj) do (
    if exist "%%D" rd /s /q "%%D" 2>nul
)

:: Define se sera self-contained (exceto para browser-wasm)
set "SELF_CONTAINED=--self-contained true"
if "%PLATFORM%"=="browser-wasm" set "SELF_CONTAINED="

:: Define diretorio de saida
set "OUTPUT_DIR=Build\VNTextPatch-%PLATFORM%"
echo Publishing for platform: %PLATFORM%

:: Publicacao
:: Flags extras que podem ser adicionadas se precisar:
::   -p:PublishSingleFile=true
::   -p:PublishAot=true
dotnet publish VNTextPatch\VNTextPatch.csproj ^
  -c Release ^
  -r "%PLATFORM%" ^
  -o "%OUTPUT_DIR%" ^
  -p "PublishSingleFile=true" ^
  %SELF_CONTAINED%

:: Compacta a pasta de saida em um .zip (opcional - descomente para usar)
:: set "ZIP_FILE=%OUTPUT_DIR%.zip"
:: echo Compactando %OUTPUT_DIR% para %ZIP_FILE%...
:: powershell -NoProfile -Command "Compress-Archive -Path '%OUTPUT_DIR%\*' -DestinationPath '%ZIP_FILE%' -Force"
:: echo Publicacao e compactacao concluidas: %ZIP_FILE%

endlocal