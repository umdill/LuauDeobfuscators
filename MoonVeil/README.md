# MoonVeil Deobfuscator
Wow this took a little while

## Usage

```bat
run.bat script.lua
```
outputs

- `script.deobfuscated.lua` - reconstructed payload/source view
- `script.deobfuscated.lua.reconstruction.txt` - confidence/limitations
- `script.deobfuscated.payload.decoded.bin` - decoded VM payload
- `script.deobfuscated.payload.prototypes.txt` - recovered prototype records and constant pools
- `script.deobfuscated.wrapper.lua` - old wrapper cleanup (kept only as a secondary artifact)

Decompiler outputs
- .payload.disasm.txt: inferred 32-bit instruction layouts and operands
- .payload.cfg.txt: control-flow / relative-target candidates
- .payload.opcodes.txt: cross-prototype opcode frequency and role hints
- .payload.headers.txt: inferred variable prototype header bytes
- .payload.constants.txt: recovered constant pools with generic classifications

The decompiler does not use sample-specific source templates. Unknown opcode semantics remain op_XX until a mapping is proven or added.
