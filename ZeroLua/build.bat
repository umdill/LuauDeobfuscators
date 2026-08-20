@echo off
setlocal
cd /d "%~dp0"
if exist publish rmdir /s /q publish
dotnet publish ZeroLuaDeobfuscator.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Built: %~dp0publish\ZeroLuaDeobfuscator.exe