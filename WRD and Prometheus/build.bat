@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 8 SDK was not found.
    echo Install the .NET 8 SDK, then run this file again. README for more info.
    pause
    exit /b 1
)

echo Building WRD/Prometheus Deobf
dotnet restore
if errorlevel 1 goto :fail

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
if errorlevel 1 goto :fail

echo.
echo Built iiin
echo   bin\Release\net8.0\win-x64\publish\WRD-Deobfuscator.exe
echo.
echo Drag a .lua, .luau, or .txt file onto the EXE.
pause
exit /b 0

:fail
echo.
echo BUILD FAILED!!
pause
exit /b 1
