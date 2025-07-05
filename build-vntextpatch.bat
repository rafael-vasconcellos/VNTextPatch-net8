@echo off

set DOTNET_CLI_TELEMETRY_OPTOUT=1

for /F "tokens=*" %%f in ('dir /B /AD /S bin') do rmdir /S /Q "%%f"
for /F "tokens=*" %%f in ('dir /B /AD /S obj') do rmdir /S /Q "%%f"
if exist Build\VNTextPatch rmdir /S /Q Build\VNTextPatch
if exist Debug rmdir /S /Q Debug
if exist Release rmdir /S /Q Release

mkdir Build
mkdir Build\VNTextPatch

dotnet restore VNTextPatch\VNTextPatch.csproj /p:RuntimeIdentifiers=win
msbuild VNTextPatch\VNTextPatch.csproj /p:LangVersion=9 /p:AllowUnsafeBlocks=true /p:Platform=AnyCPU /p:Configuration=Release /p:OutputPath=..\Build\VNTextPatch\
#del Build\VNTextPatch\FreeMote*.xml
del Build\VNTextPatch\*.pdb
del Build\VNTextPatch\*.txt

PAUSE