@echo off

set DOTNET_CLI_TELEMETRY_OPTOUT=1

REM Limpeza dos diretórios antigos
for /F "tokens=*" %%f in ('dir /B /AD /S bin') do rmdir /S /Q "%%f"
for /F "tokens=*" %%f in ('dir /B /AD /S obj') do rmdir /S /Q "%%f"
if exist Build\VNTextPatch rmdir /S /Q Build\VNTextPatch
if exist Debug rmdir /S /Q Debug
if exist Release rmdir /S /Q Release

mkdir Build

REM Restaura dependências
dotnet restore VNTextPatch\VNTextPatch.csproj

REM Compila em Release para a pasta Build\VNTextPatch
dotnet build VNTextPatch\VNTextPatch.csproj -c Release -o Build\VNTextPatch

REM Remove arquivos indesejados
del Build\VNTextPatch\*.pdb
del Build\VNTextPatch\*.txt

PAUSE