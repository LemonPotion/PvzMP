# tools/re - native reverse-engineering tools

These eight scripts read the IL2CPP build directly. They are the oracle for
"does this native method really do X?". C# decompilation is useless here:
`MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll` is only Il2CppDumper proxy
stubs (every method body is `{ }`). The real logic is x86-64 machine code in
`GameAssembly.dll`. These tools map a method name to its bytes and let you
scan the code.

## Why `py -3` and nothing else

On this machine the `python` and `python3` shims are broken Windows Store
stubs. Always invoke as:

    py -3 tools/re/<script>.py <args>

Git Bash resets the working directory to the game folder after every command,
so prefix the repo too:

    cd /d/PvzMP && py -3 tools/re/pvz-disasm.py "CreateItem$$SetCoin"

## Why output is ASCII-only

The Windows console is cp1251. Native identifiers are matched with regexes that
can carry non-ASCII text. Scripts that could emit it route through a
`show(text)` helper that does `text.encode("ascii", "replace")`. Never print
raw non-ASCII; redirect to a file and read it with the Read tool if a dump is
big.

## Hardcoded input paths

Every script opens two files by absolute path at the top:

    DLL    = D:\PvzFusion\GameAssembly.dll
    SCRIPT = D:\PvzMP\ref\dump-en\script.json

`script.json` and the other dumps under `ref/dump-en/` are on disk but EXCLUDED
from git (`.git/info/exclude`). They describe the en-4.1 build. To probe the
second install, edit the `DLL` constant to `D:\PvzFusion2\GameAssembly.dll`
(same build, same addresses).

## Naming convention

`script.json` -> `ScriptMethod[].Name` uses the form `Class$$Method` (double
dollar). Lambdas and compiler-generated members use `.` and `<Name>b__NN`.
All name arguments below are plain substrings of that `Name`, matched with
Python `in`, not regex, EXCEPT pvz-pat/pvz-scan/pvz-pair which take bytes/offsets.

So to hit `Mouse.TryToSetPlantByCard` pass the substring `TryToSetPlantByCard`,
or `Mouse$$TryToSetPlantByCard` to be exact.

## Methods: size, callers, calls

Address is a decimal RVA. A method's size is the gap to the next method by
address (sorted across ALL methods), capped (4KB for pvz-hex, 8KB for the rest).
`rva2off` translates RVA -> file offset through the PE section table.
`pvz-index.py` is the one exception: it stops the body at the first `int3`
padding run, which is the real code length (see EVIDENCE.md E26).

---

## pvz-index.py --stats | --candidates | --arbiter | --class | --method | --calibrate | --write

The atlas: every one of the 204,331 methods with a body, classified, so coverage
is chosen from a complete list instead of a search. It joins `script.json` (which
has no image field) to dump.cs by name and settles every collision with the
address dump.cs prints as `// RVA:`; then it scans each game method's bytes for
field stores, calls, string literals and rip-relative references.

Per method: `rva`, image, parameter categories (`int obj bool float string val
arr` and the unsendable `hard`), signature format, name class (verb / accessor /
frame callback / generated), body features and one `effect` word
(`world config side read pure blocked frame gen`).

    py -3 tools/re/pvz-index.py --stats        # counts, join quality, why they differ
    py -3 tools/re/pvz-index.py --candidates   # arbiter shortlist: verbs per class
    py -3 tools/re/pvz-index.py --class Board Zombie
    py -3 tools/re/pvz-index.py --method Board@SetSun   # bytes, calls, refs, strings
    py -3 tools/re/pvz-index.py --calibrate    # noise floor + 13 known-answer rows
    py -3 tools/re/pvz-index.py --write        # ref/native-index.json (71MB, not in git)

    py -3 tools/re/pvz-index.py --arbiter      # prove every Core/Session/Coverage.cs row
    py -3 tools/re/pvz-index.py --arbiter write # and emit Core/Session/CoverageFacts.cs

`--arbiter` is the coverage table's other half. `Core/Session/Coverage.cs` is the
decision (which native method the room covers, written by hand, one row per synced
feature); everything a row claims about the build - the name, the arity, the address,
the body length, the calls, the effect and whether the body reaches
`UnityEngine.Random` - is measured here and emitted as `CoverageFacts.cs`. A row the
build no longer answers is a PROBLEM and the file is not written, which is the only
reason a peer can never be sent a call a renamed method would swallow. Two rows are
refused on principle: a `Replicate` on a `frame`/`gen`/`blocked` body, and a
`Replicate` on a body that rolls, because a replay of a roll is a different world on
every end (E35).

    py -3 tools/re/pvz-index.py --arbiter
    key                                rva        format                 effect   verdict   rolls  authority
    Board.SetEvePlants/3               0x47c6f0   obj T(int,int,bool)    world    Never     yes    HostOnly
    Board.SetSun/1                     0x481250   void T(int)            world    Replicate -      HostOnly
    Mouse.SetPlantFromCard/3           0x75e110   obj T(int,int,int)     world    Replicate -      AnyPeer

`ref/NATIVE-INDEX.md` is the human summary of the same data.

---

## pvz-disasm.py "<name>" ["<name>" ...]

For each method whose name contains the substring: print `rva`/`size`, then
every static call in its body. Calls are `E8`/`E9` rel32; the target address is
resolved back to a name via the address index. Shows the byte offset in the body
(`+0x23A -> CreateItem$$SetCoin`).

Use: "what does this method call?" Answers fan-out questions, e.g. that
`Board.SunUpdate` calls `CreateItem.SetCoin` exactly 8 times (8 sky-sun rolls).

    py -3 tools/re/pvz-disasm.py "Board$$SunUpdate"

## pvz-xref.py "<name>" ["<name>" ...]

Reverse of disasm: finds every static caller of the matched method(s). It walks
ALL method bodies looking for `E8`/`E9` that land on a target address. Prints
`Caller$$Method+0xOFF` per site, or "no static caller found".

Slow (scans every body). Use: "who calls this / is this the only mint door?".
Proved `CreateItem.SetCoin` has 204 call-sites (the single coin door) and
`InGameUI.OnStartBattleButtonClick` has ZERO callers (so gating only it is
useless; the real button is `InGameBtn.OnMouseUpAsButton`).

    py -3 tools/re/pvz-xref.py "CreateItem$$SetCoin"

## pvz-hex.py "<name>" ["<name>" ...]

Hex dump (16 bytes per row, body-relative offset) of each matched method, first
4096 bytes. Use to read the raw instructions pvz-disasm skips (writes, compares,
branches). Read `+0x502` there to see the `mov dword [rax+0x18C], 0x40400000`
(battle start sets timeUntilNextWave = 3.0f).

    py -3 tools/re/pvz-hex.py "InGameBtn$$OnMouseUpAsButton"

## pvz-scan.py "<disp>:<imm>:<width>" ["..." ...]

Finds direct stores of a constant into a field: `C7 /0` (mov dword [reg+disp32],
imm32) and `C6 /0` (mov byte [reg+disp32], imm8). Arguments are
`offset:constant:width` where offset is hex, constant is base-0 (`0x3ED` or `3`),
width is `4` or `1`. Prints each writing method+offset.

Use: "who writes this field and to what value?". Only matches disp32 stores.

    py -3 tools/re/pvz-scan.py "18C:0x40400000:4"

## pvz-pat.py "<hexspec>" ["..." ...]

Raw byte-pattern search over the whole DLL, prints owning method+offset. A spec
is a hex string split into 2-hex-byte chunks; `..` is a full-wildcard byte. The
`A|lo-hi|B` form inserts a variable-length gap: bytes `A`, then `lo..hi` filler
bytes, then `B` (the gap is dash-separated; `.` matches every byte, `0x0A`
included).

This is how you confirm a byte-level mechanic claim regardless of which method
contains it. Confirmed `83F813` (cmp eax,0x13 -> 20-button dispatch) and
`C7808C01000000004040` (timeUntilNextWave=3.0f write) both inside
`OnMouseUpAsButton`, and `F30F11|1-2|F0010000` (14 sites, 10 of them game code)
is how the stores into `Plant.attributeCountdown` were located - the clock's own
writer is `Plant.SetAttributeCountdownValue+0x38` (ref/EVIDENCE.md E43).

    py -3 tools/re/pvz-pat.py "C7808C01000000004040"
    py -3 tools/re/pvz-pat.py "F30F11|1-2|F0010000"

## pvz-pair.py "<src_hex>" "<dst_hex>"

Finds float copy windows: a `F3 0F 10 <modrm> <disp=src>` (movss xmm, [reg+src])
followed within 32 bytes by `F3 0F 11 <modrm> <disp=dst>` (movss [reg+dst], xmm)
of the SAME xmm register. Arguments are two FIELD OFFSETS in hex (the disp byte),
NOT names. Prints owning method+offset of the load and the store.

Use: "does the code copy float field A into float field B?" (e.g. fullCD minus a
constant into CD, the cooldown path).

    py -3 tools/re/pvz-pair.py 48 44

---

## pvz-nulluse.py "<Class$$Method>" ["--only=<caller substring>"]

Answers "can this door be skipped?". A Harmony prefix that returns false makes the native body never
run, so an object-returning method hands its caller a null pointer. Whether that is a refusal or a
crash is not a property of the door - it is a property of every caller it has. The script walks every
static call site of the named method in the whole image and reads what the caller does with `rax`, the
register the answer arrives in, before anything else touches it:

    checked   a test or compare of rax comes first, so the caller looks at the answer
    dropped   the next call or branch is reached with rax untouched - the answer was discarded
    returned  the body ends before touching it
    deref     a memory operand based on rax: a field read or write through the answer
    passed    mov rcx/rdx/r8/r9, rax: the answer becomes the next call's `this` or an argument
    stored    mov [rbp+x], rax - kept in a local and used later, which this does not follow

`deref` and `passed` are printed as the dangerous list, because those are the bodies a skipped door
crashes. Use this before gating anything that returns an object; the answer so far is that only `void`
doors get gated (ref/EVIDENCE.md E43).

    py -3 tools/re/pvz-nulluse.py "CreatePlant$$SetPlant"
    py -3 tools/re/pvz-nulluse.py "CreatePlant$$SetPlant" --only=Paintingflower

---

## x64 calling convention (for reading the dumps by hand)

- Integer/pointer args: rcx = this, then rdx, r8, r9, then [rsp+0x20], [rsp+0x28].
- Float args: xmm0..xmm3 in the same positional slots.
- il2cpp `methodInfo` arg is usually zeroed (`xor r9d, r9d`).
- Delegate invoke: `call qword [rbx+0x70]` (Action.Invoke).
- Indirect `FF 15` calls do NOT resolve statically; treat them as unknown.

## Recording a fact

Any fact you prove with these tools belongs in `ref/EVIDENCE.md` as
"symbol -> RVA -> size -> what it proves". A fact that lives only in a comment
in one method counts as lost.
