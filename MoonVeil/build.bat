@echo off
setlocal
cd /d "%~dp0"
dotnet build -c Release
if errorlevel 1 exit /b 1
echo.
echo Build complete.