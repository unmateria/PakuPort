# 32-bit instruction test vectors

Ground truth: **Unicorn**, an x86 implementation independent from PakuPort's. Nobody writes the
"expected" values by hand.

```powershell
python tools\gen_opcode_vectors32.py                    # regenerate the vectors
dotnet test tests\PakuPort.Tests --filter Interp32Tests # measure the interpreter against them
```

| measurement | when | result |
|---|---|---|
| initial RED | before widening the core to 32 bits | **38 / 1677** |
| GREEN | after widening `Cpu` and `Interp` | **1677 / 1677** |

## What it measures, exactly

1,677 vectors in 80 families. Each one sets up the **same test bench** in both worlds (code at
`0000:2000`, a 64-byte scratch area at `0000:0100` filled with a known pattern, stack at
`0000:1000`), runs it and compares:

- the **eight 32-bit registers** (`EAX`…`ESP`), not just AX/DX — a width bug lives in the upper
  half, which the 16-bit suite (`gen_opcode_vectors.py`) never looks at;
- **two memory windows**: the scratch area and the stack (so push/pop/pushad/popad and the string
  instructions are covered);
- the flags the **Intel SDM defines** for that opcode. Undefined flags are not compared: each
  vector carries its own `check` list; without it the suite would be measuring Unicorn's whims.

Families covered: binary and unary ALU, shifts and rotates (including counts 0, 16, 31, 32 and 33
and the count in CL), `shld`/`shrd`, `mul`/`imul` (1, 2 and 3 operands), `div`/`idiv`,
`cwde`/`cdq`, `movzx`/`movsx`, `bt`/`bts`/`btr`/`btc`, `bsf`/`bsr`, `mov`/`xchg`/`lea`, 32-bit
memory operands **and 32-bit addressing with scale**, 32-bit stack, `pushfd`/`popfd`, strings
(`movsd`/`stosd`/`lodsd`/`scasd`/`cmpsd` with and without `rep`, in both DF directions) and
`jecxz`.

**8- and 16-bit** vectors are mixed in on purpose (`cbw`, `cwd`, `push16`, `rep movsb`,
`repne scasb`, `pushf`, `loop`): they are the anti-regression net for the world that already
worked, measured by the same judge.

## Harness decisions that cost something

**It runs in `UC_MODE_16`, not in 32-bit mode.** That is the real-mode-plus-`0x66`-prefix world
most DOS games run in, so the vector also validates PakuPort's 16-bit decoding path (Iced), which
is where a DOS game's 32-bit code comes in. ALU semantics do not depend on the mode.

**Every snippet is disassembled again with capstone and the text travels in the vector.** Not
decoration: keystone in 16-bit mode accepts `mov eax,[ebx*4+0x100]` and **silently** emits the
16-bit form `mov eax,[0x100]` — no `0x67` prefix and no index. That vector would have gone GREEN
while testing a different instruction. The two cases keystone cannot assemble (32-bit addressing,
and the string `movsd` that it confuses with the SSE one) are given as `raw:<hex>`, checked with
capstone before they go in.

**Unicorn's `count` counts ITERATIONS of a `rep`, not instructions.** With `count=1` a
`rep movsd` stops mid-copy while the interpreter finishes it, and the 8 string vectors came out
red because of that — a harness artifact, not a core bug. It is given plenty of margin, and what
really stops execution is the snippet's end address (`until` in `emu_start`).

**`pushfd` is compared masked** (`0x7ED7`): it leaves bits on the stack that PakuPort does not
model (TF), and not modelling them is not a bug.

## What this suite does NOT cover

- `iretd`, `lss` and the protected-mode instructions (`verr`/`lar`/`lsl`/`sldt`/`smsw`/`sidt`/
  `arpl`). There is no way to set up an interrupt frame or a descriptor inside an opcode vector,
  so none of them is measured here.
- 32-bit `in`/`out`: no DOS device uses 32-bit ports.
- near `call`/`jmp` with a 32-bit operand, 32-bit `enter`/`leave`, and string instructions with
  32-bit addressing (ESI/EDI/ECX instead of SI/DI/CX). They do not appear in real mode.
- The **lifter** stays 16-bit on purpose: the product's engine is the interpreter.

## `push sp`

When this suite was created, 16-bit `push sp` followed the 8086 semantics (it pushed the **new**
SP), so there is deliberately no `push sp` vector. It has since been changed to the 286-and-later
semantics (the SP from **before** the instruction) in both `Interp` and `Lifter` — see the comment
on `push sp` in `src/PakuPort.Core/Interp.cs`.

`push esp` (32-bit) pushes the previous value, as a 386 does, and has a vector.
