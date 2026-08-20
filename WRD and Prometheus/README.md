# WRD AND Prometheus Deobfuscator

C#/.NET 8 deobfuscator for the WeAreDevs/Prometheus v1 Lua wrapper.

## Requires

- .NET 8 SDK (https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe)
- Windows, Linux, or macOS for command-line use


## Build

Run `build.bat`. The exe is at

`bin\Release\net8.0\win-x64\publish\WRD-Deobfuscator.exe`

Drag a `.lua`, `.luau`, or `.txt` file onto the EXE. Output is written beside the input as `{x}_deobfuscated.lua`.

## How it works

Decodes the shuffled string table, folds arithmetic, traces the VM with execution limits, searches around anti-tamper states, and reconstructs closures and calls ONLY when the VM preserves enough information.

Original comments, formatting, and names are never recovered b/c prometheus removes them. 


## Notes

Shouldn't require manual cleanup.