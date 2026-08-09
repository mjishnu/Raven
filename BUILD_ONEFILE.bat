@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "ROOT=%CD%"
set "BUILD=%ROOT%\_onefile_build"
set "PAYLOAD=%BUILD%\payload"
set "UPDATER=%BUILD%\updater"
set "LAUNCHER=%BUILD%\launcher"
set "PAYLOADZIP=%BUILD%\RavenPayload.zip"
set "OUTPUT=%ROOT%\OUTPUT"
set "NUGET=https://api.nuget.org/v3/index.json"

echo ============================================================
echo Raven Portable - x64 OneFile Builder
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: .NET SDK was not found.
    echo Install the .NET 10 SDK and run this file again.
    goto :fail
)

for /f "tokens=1 delims=." %%V in ('dotnet --version') do set "DOTNET_MAJOR=%%V"
if not "%DOTNET_MAJOR%"=="10" (
    echo ERROR: .NET 10 SDK is required. Found:
    dotnet --version
    goto :fail
)

where git >nul 2>nul
if not errorlevel 1 (
    echo Updating Git submodules...
    git submodule update --init --recursive
    if errorlevel 1 goto :fail
) else (
    if not exist "StoreListings\StoreListings.Library\StoreListings.Library.csproj" (
        echo ERROR: Git is not installed and the StoreListings submodule is missing.
        goto :fail
    )
)

if exist "%BUILD%" rmdir /s /q "%BUILD%"
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"
mkdir "%PAYLOAD%" || goto :fail
mkdir "%UPDATER%" || goto :fail
mkdir "%LAUNCHER%" || goto :fail
mkdir "%OUTPUT%" || goto :fail

echo.
echo [1/6] Restoring Raven packages from nuget.org...
dotnet restore "Raven\Raven.csproj" -r win-x64 --source "%NUGET%"
if errorlevel 1 goto :fail

echo.
echo [2/6] Publishing Raven self-contained x64 payload...
dotnet publish "Raven\Raven.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  --no-restore ^
  -p:Platform=x64 ^
  -p:WindowsAppSDKSelfContained=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "%PAYLOAD%"
if errorlevel 1 goto :fail

echo.
echo [3/6] Publishing self-contained Raven updater...
dotnet restore "Raven.Updater\Raven.Updater.csproj" -r win-x64 --source "%NUGET%"
if errorlevel 1 goto :fail

dotnet publish "Raven.Updater\Raven.Updater.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  --no-restore ^
  -p:PublishSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "%UPDATER%"
if errorlevel 1 goto :fail

del /q "%PAYLOAD%\Raven.Updater.*" >nul 2>nul
if not exist "%UPDATER%\Raven.Updater.exe" (
    echo ERROR: Raven.Updater.exe was not produced.
    goto :fail
)
copy /y "%UPDATER%\Raven.Updater.exe" "%PAYLOAD%\Raven.Updater.exe" >nul
if errorlevel 1 goto :fail

del /s /q "%PAYLOAD%\*.pdb" >nul 2>nul

if not exist "%PAYLOAD%\Raven.exe" (
    echo ERROR: Raven.exe was not produced in the self-contained payload.
    goto :fail
)

echo.
echo [4/6] Compressing embedded Raven payload...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; Compress-Archive -Path '%PAYLOAD%\*' -DestinationPath '%PAYLOADZIP%' -CompressionLevel Optimal -Force"
if errorlevel 1 goto :fail

echo.
echo [5/6] Building single Raven-Portable.exe...
dotnet restore "Raven.OneFileLauncher\Raven.OneFileLauncher.csproj" ^
  -r win-x64 ^
  --source "%NUGET%" ^
  "-p:RavenPayloadZip=%PAYLOADZIP%"
if errorlevel 1 goto :fail

dotnet publish "Raven.OneFileLauncher\Raven.OneFileLauncher.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  --no-restore ^
  -p:PublishSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  "-p:RavenPayloadZip=%PAYLOADZIP%" ^
  -o "%LAUNCHER%"
if errorlevel 1 goto :fail

if not exist "%LAUNCHER%\Raven.OneFileLauncher.exe" (
    echo ERROR: OneFile launcher was not produced.
    goto :fail
)

copy /y "%LAUNCHER%\Raven.OneFileLauncher.exe" "%OUTPUT%\Raven-Portable.exe" >nul
if errorlevel 1 goto :fail

echo.
echo [6/6] Calculating SHA256...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$h=(Get-FileHash '%OUTPUT%\Raven-Portable.exe' -Algorithm SHA256).Hash; Set-Content -Path '%OUTPUT%\Raven-Portable.exe.sha256.txt' -Value ($h + '  Raven-Portable.exe') -Encoding ASCII; Write-Host ('SHA256: ' + $h)"
if errorlevel 1 goto :fail

echo.
echo ============================================================
echo BUILD SUCCESSFUL
echo ============================================================
echo Output:
echo   %OUTPUT%\Raven-Portable.exe
echo.
echo This is the only EXE required for distribution.
echo The SHA256 text file is optional and can be published beside it.
echo.
pause
exit /b 0

:fail
echo.
echo ============================================================
echo BUILD FAILED
echo ============================================================
echo Review the error above.
echo.
pause
exit /b 1
