@echo off
setlocal
cd /d "%~dp0"
if "%~1"=="" (
  echo usage: run.bat input.lua [output.lua] OR drag
  exit /b 1
)
dotnet run -c Release -- %*
