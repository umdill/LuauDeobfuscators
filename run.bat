@echo off
setlocal
cd /d "%~dp0"

if "%~1"=="" (
    echo Drag a .lua file onto this file to deobfuscate it.
    echo.
    pause
    exit /b 1
)

dotnet run --project "%~dp0ZeroLuaDeobfuscator.csproj" -c Release --nologo --verbosity quiet -- "%~1"
if errorlevel 1 pause
