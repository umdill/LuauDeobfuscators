# Zero Lua Deobfuscator
Decodes the constant pool and VM prototypes, reconstructs Lua expressions and closures, resolves supported upvalues, and simplifies generated control flow if possible.

## Requirements

- .NET 8 SDK (https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe)
- Windows, Linux, or macOS for command-line use

## Usage

### Windows

Drag a lua file onto run.bat.

```text
script.deobfuscated.lua
```

### Command line

```bash
dotnet run -c Release -- "input.lua"
```

Choose the output:

```bash
dotnet run -c Release -- "input.lua" "output.lua"
```

After publishing the project via dotnet, the executable can be used in itself

```text
ZeroLuaDeobfuscator.exe input.lua output.lua
```

## Notes

Requires manual cleanup afterwards, im not yo slave!! 
