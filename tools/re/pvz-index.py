"""Build a machine-readable index of every native method in the game build.

Phase 1 product: the decompiled game turned into DATA, so the sync layer can be
generated from it instead of being hand-written one feature at a time. It reads;
it does not guess.

Inputs (absolute paths, like the other pvz-* tools):
  ref/dump-en/script.json          Il2CppDumper method table: Name, Address (RVA),
                                   native C Signature, TypeSignature (stack slots)
  ref/dump-en/dump.cs              managed declarations: image per type, field
                                   offsets, parameter names, access modifiers
  D:\\PvzFusion\\GameAssembly.dll   the real x86-64 bodies

Output:
  ref/native-index.json            derived data, one record per method; stays out of git
  stdout                           ASCII only (the console is cp1251)

Usage:
  py -3 tools/re/pvz-index.py --types                 type-token alphabet of the build
  py -3 tools/re/pvz-index.py --sig CreateItem         raw native signatures, by substring
  py -3 tools/re/pvz-index.py --stats                  counts vs the measured baseline
  py -3 tools/re/pvz-index.py --calibrate              noise floor + known-answer checks
  py -3 tools/re/pvz-index.py --class Board Zombie     every method of those classes
  py -3 tools/re/pvz-index.py --method Board@GetSun     one record, with its raw call list
  py -3 tools/re/pvz-index.py --candidates             arbiter candidate rows
  py -3 tools/re/pvz-index.py --arbiter [write]        prove Core/Session/Coverage.cs rows
  py -3 tools/re/pvz-index.py --write [Board ...]      write ref/native-index.json
"""
import json, re, struct, sys, bisect, collections, time

DLL = r"D:\PvzFusion\GameAssembly.dll"
SCRIPT = r"D:\PvzMP\ref\dump-en\script.json"
DUMPCS = r"D:\PvzMP\ref\dump-en\dump.cs"
OUT = r"D:\PvzMP\ref\native-index.json"
COVERAGE_CS = r"D:\PvzMP\Core\Session\Coverage.cs"
FACTS_CS = r"D:\PvzMP\Core\Session\CoverageFacts.cs"

GAME_IMAGES = {"Assembly-CSharp.dll"}
# The RH build ships its helper library next to the simulation. Checked 2026-11-11:
# 24 types, all static math/string/collection helpers, no world state.
FIRST_PARTY_OTHER = {"Core.dll"}      # the game's own code: image 0
BODY_SIZE_CAP = 8192                        # same cap the other pvz-* tools use
CALL_NEAR = 64                              # rel32 counts if this close to a body start
MAX_LIST = 64                               # cap per stored list

# ---- parameter wire categories ------------------------------------------------
#   i int/char  b bool  f float  s string  o object reference  t value type by value
#   a array  e enum (native sees int32_t, dump.cs names the enum type)
#   d delegate  p pointer/IntPtr  g shared generic  k class ref  ? unrecognized
# Everything in HARD_CATS cannot travel as a value, so coverage must skip such a
# method instead of inventing a payload for it.
CAT_NAME = {"i": "int", "b": "bool", "f": "float", "s": "string", "o": "obj",
            "t": "val", "a": "arr", "e": "int", "d": "hard", "p": "hard",
            "g": "hard", "k": "hard", "?": "hard", "v": "void"}
HARD_CATS = set("dpgk?")
INT_TYPES = {"int8_t", "uint8_t", "int16_t", "uint16_t", "int32_t", "uint32_t",
             "int64_t", "uint64_t", "char", "unsigned char", "signed char"}
POINTER_TYPES = {"intptr_t", "uintptr_t", "void*", "Il2CppMethodPointer"}

# ---- name classes -------------------------------------------------------------
VERB_PREFIXES = ("Set", "Add", "Create", "Die", "Kill", "Spawn", "Summon", "Release",
                 "Apply", "Change", "Move", "Drop", "Buy", "Use", "Pick", "Put",
                 "Start", "Stop", "Open", "Close", "Clear", "Give", "Take", "Damage",
                 "Heal", "Freeze", "Unlock", "Complete", "Show", "Hide", "Draw", "Pay",
                 "Save", "Load", "Capture", "Reset", "Restart", "Init")
# Per-frame and raw input callbacks: never re-sent, their result travels as a
# snapshot. OnMouse*/OnPointer* stay hand-gated - see NATIVE-INDEX.md section 5.
# Prefix matching only the On* family is safe because no game command is spelled
# that way; the five lifecycle names are NOT prefixes, or UpdateText and a plain
# class's own Start() would be filed as engine callbacks.
UNITY_LIFECYCLE = {"Update", "FixedUpdate", "LateUpdate", "Awake", "Start"}
FRAME_PREFIXES = ("OnTrigger", "OnPointer",
                  "OnMouse", "OnDrag", "OnBeginDrag", "OnEndDrag", "OnScroll",
                  "OnCollision", "OnParticle", "OnJoint", "OnAnimation", "OnAnimator",
                  "OnSelect",
                  "OnDeselect", "OnMove", "OnSubmit", "OnCancel", "OnHover", "OnEnter",
                  "OnExit", "OnEnable", "OnDisable", "OnPreRender", "OnPostRender",
                  "OnAudioFilterRead", "OnValidate", "OnDestroy", "OnLevelWasLoaded",
                  "OnApplicationPause", "OnApplicationQuit")
# What dump.cs writes in `class X : <here>` for the two types Unity drives.
MONO_ROOTS = {"MonoBehaviour", "UnityEngine.MonoBehaviour", "ScriptableObject",
              "UnityEngine.ScriptableObject"}
ACCESSOR_PREFIXES = ("get_", "set_", "add_", "remove_")
GENERATED_RE = re.compile(r"<>|__|_p_|c__DisplayClass|_d__|b__|StateMachine|"
                          r"AsyncMethodBuilder|AsyncTaskMethodBuilder|Adaptor|"
                          r"GeneratedInfo|InjectData|_Closure")
DELEGATE_RE = re.compile(r"System_Action|System_Func|System_Predicate|System_Comparison|"
                         r"System_Delegate|Delegate|EventHandler|UnityAction|Callback|"
                         r"Invoker")
# script.json `List`1<Vector3>$$Add` / `Array$$Sort<int,int>`: one of many
# instantiations of a body dump.cs declares once against the open generic.
INSTANTIATION_RE = re.compile(r"[`<]")

# ---- flag and feature bits -----------------------------------------------------
FLAG_TABLE = [("static", 1 << 0), ("public", 1 << 1), ("nonpublic", 1 << 2),
              ("game", 1 << 3), ("verb", 1 << 4), ("accessor", 1 << 5),
              ("frame", 1 << 6), ("generated", 1 << 7), ("hard_param", 1 << 8),
              ("array_param", 1 << 9), ("value_param", 1 << 10), ("ref_param", 1 << 11),
              ("enum_param", 1 << 12), ("string_param", 1 << 13), ("ctor", 1 << 14),
              ("virtual", 1 << 15), ("no_body", 1 << 16), ("managed", 1 << 17),
              ("overload_ambiguous", 1 << 18)]
# Body features. The wr_* bits mean "a store-shaped write whose displacement is
# a field offset that only that class declares, through a base register that is
# not the frame pointer" -- see world_field_sets for why the offsets are
# stripped of what Plant, Zombie and Board share. That is a screening signal,
# not a proof: run --calibrate for the noise floor, and read the owning class's
# offsets before believing a hit.
FX_TABLE = [("wr_board", 1 << 0), ("wr_plant", 1 << 1), ("wr_zombie", 1 << 2),
            ("wr_own", 1 << 3), ("random", 1 << 4), ("instantiate", 1 << 5),
            ("event", 1 << 6), ("gameapp_static", 1 << 7), ("asset_load", 1 << 8),
            ("string_lit", 1 << 9), ("scene_query", 1 << 10), ("sound", 1 << 11),
            ("coroutine", 1 << 12), ("store_any", 1 << 13), ("calls_world", 1 << 14),
            ("wr_config", 1 << 15), ("destroy", 1 << 16)]

# Classes whose state IS the shared simulation: calling one of them makes the
# caller a replication candidate.
WORLD_CLASSES = {"Board", "Plant", "Zombie", "CoinSun", "CoinMoney", "CreateItem",
                 "CreatePlant", "CreateZombie", "BoardSpawner", "BoardAction",
                 "BoardEntity", "BoardData", "BoardLayer", "BoardStatistics",
                 "SaveMgr", "GridSystem", "Mower", "Mouse", "Shovel", "InitBoard",
                 "CardSlotManager", "DrawCardManager", "Obstacle", "MiniPet",
                 "Wave", "TutorManager", "InGameUI", "Lawn", "BoardVictory",
                 "LevelData", "CardUI"}
# Native doors, spelled exactly as script.json writes them (verified 2026-11-11).
RANDOM_DOORS = ("UnityEngine.Random$$",)
INSTANTIATE_DOORS = ("UnityEngine.Object$$Instantiate",
                     "UnityEngine.GameObject$$Instantiate",
                     "UnityEngine.Component$$Instantiate",
                     "UnityEngine.ScriptableObject$$CreateInstance",
                     "UnityEngine.Object$$InstantiateAsync")
DESTROY_DOORS = ("UnityEngine.Object$$Destroy",
                 "UnityEngine.Object$$DestroyImmediate")
ASSET_DOORS = ("UnityEngine.ResourcesAPIInternal$$Load", "UnityEngine.Resources$$",
               "ResourcesManager$$", "UnityEngine.AssetBundle$$")
SCENE_DOORS = ("UnityEngine.GameObject$$Find", "UnityEngine.Object$$Find",
               "UnityEngine.Object$$Instantiate", "UnityEngine.Physics2D$$",
               "UnityEngine.Physics$$", "UnityEngine.Transform$$Find")
SOUND_DOORS = ("GameAPP$$PlaySound", "UnityEngine.AudioSource$$Play",
               "UnityEngine.AudioSource$$PlayUnclipped")
COROUTINE_DOORS = ("UnityEngine.MonoBehaviour$$StartCoroutine",
                   "UnityEngine.MonoBehaviour$$StopCoroutine")
EVENT_DOORS = ("EventManager$$Trigger",)
EVENT_INVOKE_RE = re.compile(r"(Delegate|Action|Func|EventHandler|UnityEvent)"
                             r"[^\0]*\$\$Invoke$|^\s*$")

# Numbers already measured in this repo. The index has to agree; a difference is
# a finding, not something to tune away. report_counts prints why each DIFF
# stays open instead of this table being adjusted to match.
BASELINE = {
    "methods_with_body": 204331, "game_methods": 31719, "game_types": 4909,
    "verbs": 4541, "verb_formats": 360, "verbs_hard_param": 245,
    "param_obj": 5928, "param_int": 1566, "param_float": 570, "param_bool": 433,
    "param_string": 234, "param_array": 66, "frame_callbacks": 1954,
    "accessors": 4912, "generated": 5297,
}


def show(text):
    print(str(text).encode("ascii", "replace").decode("ascii"))


def bit_names(value, table):
    return [n for n, b in table if value & b]


def fx_names(value):
    return bit_names(value, FX_TABLE)


# --------------------------------------------------------------------------- PE
def load_pe():
    data = open(DLL, "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    size_of_opt = struct.unpack_from("<H", data, pe + 20)[0]
    secs, off = [], pe + 24 + size_of_opt
    for _ in range(nsec):
        name = data[off:off + 8].rstrip(b"\0").decode()
        vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
        secs.append((vaddr, vsize, raddr, rsize, name))
        off += 40

    def rva2off(rva):
        for vaddr, vsize, raddr, rsize, _ in secs:
            if vaddr <= rva < vaddr + max(vsize, rsize):
                return raddr + (rva - vaddr)
        return None
    return data, rva2off


def load_script():
    raw = json.load(open(SCRIPT, encoding="utf-8"))
    return raw["ScriptMethod"], raw["ScriptMetadata"], raw["ScriptString"]


def split_params(text):
    """Split a parameter list on commas that are not inside <>, () or []."""
    out, depth, cur = [], 0, ""
    for ch in text:
        if ch in "<([":
            depth += 1
        elif ch in ">)]":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur.strip())
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return out


# ----------------------------------------------------------------------- dump.cs
IMG_RE = re.compile(r"^// Image (\d+): (\S+) - (\d+)")
NS_RE = re.compile(r"^// Namespace: ?(.*)$")
TYPE_RE = re.compile(r"^[\w<>.,\[\]()=:\" ]*?\b(class|struct|enum|interface)\s+"
                    r"([^\s:{]+)\s*([^;{]*?)// TypeDefIndex: (\d+)")
FIELD_RE = re.compile(r"^([\w<>.,\[\] \?]*?)([A-Za-z_][\w<>.,\[\]]*)\s*"
                      r"(?:=[^;]*)?;\s*//\s*0x([0-9A-Fa-f]+)$")
RVA_RE = re.compile(r"// RVA: (0x[0-9A-Fa-f]+|-1)")
MODIFIERS = {"public", "private", "protected", "internal", "static", "virtual",
             "override", "abstract", "sealed", "extern", "async", "readonly",
             "new", "unsafe", "event", "implicit", "explicit", "fixed"}


def parse_dump_cs():
    """-> (types keyed by qualified name, image markers)

    dump.cs names a type by what follows `class`: a top-level type as its
    simple name with the namespace on the `// Namespace:` line above it, and a
    nested or compiler-generated type as `Outer.Inner` with that line empty.
    script.json always writes the fully qualified, open-named token, so the
    join runs through `type_index` rather than these keys directly.
    """
    types, images = {}, []
    cur_type, pending_rva, pending_ns = None, None, ""
    with open(DUMPCS, encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            line = raw.rstrip("\n")
            m = IMG_RE.match(line)
            if m:
                images.append((int(m.group(3)), m.group(2)))
                continue
            if not line.startswith("\t"):
                m = NS_RE.match(line)
                if m:
                    pending_ns = m.group(1).strip()
                    continue
                m = TYPE_RE.match(line)
                if m:
                    kind, tname, tail = m.group(1), m.group(2), m.group(3)
                    tidx = int(m.group(4))
                    base = tail.split(":", 1)[1] if ":" in tail else ""
                    tname = tname.replace("+", ".")
                    if pending_ns and not tname.startswith(pending_ns + "."):
                        fq = "%s.%s" % (pending_ns, tname)
                    else:
                        fq = tname
                    cur_type = fq
                    t = types.get(fq)
                    if t:
                        t["tidx"] = tidx
                    else:
                        types[fq] = {"fq": fq, "name": tname, "ns": pending_ns,
                                     "kind": kind, "tidx": tidx, "image": None,
                                     "base": base.strip(), "fields": {},
                                     "statics": {}, "methods": []}
                    pending_rva, pending_ns = None, ""
                continue
            if cur_type is None:
                continue
            s = line.strip()
            if s.startswith("//"):
                m = RVA_RE.search(s)
                if m:
                    pending_rva = m.group(1)
                continue
            if s.endswith("{ }") and "(" in s:
                d = parse_method_decl(s)
                if d:
                    d["rva"] = pending_rva
                    d["addr"] = int(pending_rva, 16) if pending_rva \
                        and pending_rva != "-1" else 0
                    types[cur_type]["methods"].append(d)
                pending_rva = None
                continue
            m = FIELD_RE.match(s)
            if m and "(" not in s:
                decl, name, off = m.group(1), m.group(2), int(m.group(3), 16)
                is_static = re.search(r"\bstatic\b", decl) and \
                    not re.search(r"\bconst\b", decl)
                (types[cur_type]["statics"] if is_static
                 else types[cur_type]["fields"])[name] = off
    for t in types.values():
        t["image"] = image_of(t["tidx"], images)
    return types, images


def parse_method_decl(text):
    """'public static void Foo(int a, bool b) { }' -> dict of the pieces."""
    head, _, rest = text.partition("(")
    params_src = rest.partition("){ }")[0]
    toks = head.strip().split()
    if not toks:
        return None
    name = toks[-1]
    if "<" in name:
        name = name[:name.index("<")]
    mods, ret_pos = [], None
    for i, t in enumerate(toks[:-1]):
        if t in MODIFIERS:
            mods.append(t)
        elif ret_pos is None:
            ret_pos = i
    ret = " ".join(toks[ret_pos:-1]) if ret_pos is not None else "void"
    params = []
    for p in split_params(params_src):
        p = re.sub(r"^(ref|out|in|params) ", "", p).strip()
        if not p:
            continue
        m = re.match(r"^(.*\s)([A-Za-z_]\w*)$", p)
        params.append((m.group(1).strip(), m.group(2)) if m else (p, ""))
    return {"name": name, "mods": " ".join(mods), "ret": ret, "params": params,
            "static": "static" in mods,
            "virtual": any(k in mods for k in ("virtual", "override", "abstract"))}


def image_of(tidx, images):
    cur = None
    for start, name in images:
        if tidx >= start:
            cur = name
    return cur


# -------------------------------------------------------------------- signature
MI_RE = re.compile(r"MethodInfo_[0-9A-Fa-f]+\b")
DECL_NAME_RE = re.compile(r"^(.*[\s\*])([A-Za-z_][A-Za-z0-9_]*)$")


def norm_type(decl):
    """'Board_o* board' -> 'Board_o*', 'const MethodInfo_3ED8E0* method' -> 'const MethodInfo*'."""
    decl = decl.strip()
    m = DECL_NAME_RE.match(decl)
    if m and "[]" not in decl:
        decl = m.group(1).strip()
    return MI_RE.sub("MethodInfo", decl)


def cat_native(tok, enum_names, delegate_names):
    t = tok.replace("const ", "").strip()
    if t == "void":
        return "v"
    if t == "bool":
        return "b"
    if t in INT_TYPES:
        return "i"
    if t in ("float", "double"):
        return "f"
    if t == "System_String_o*":
        return "s"
    if t.endswith("_array*") or t == "System_Array_o*":
        return "a"
    if "__Il2CppFullySharedGenericType" in t:
        return "g"
    bare = t.rstrip("*")
    if DELEGATE_RE.search(t) or bare in delegate_names:
        return "d"
    if t.endswith("_c*") or t.endswith("_c"):
        return "k"
    if t.endswith("_o*") or t == "Il2CppObject*":
        return "o"
    if t.endswith("_o") or t.endswith("_v"):
        return "t"
    if t in POINTER_TYPES or t.endswith("*"):
        return "p"
    return "?"


VALUE_TYPES = {"Vector2", "Vector3", "Vector4", "Quaternion", "Color", "Color32",
               "DateTime", "TimeSpan", "Rect", "Bounds", "Ray", "Matrix4x4",
               "Vector2Int", "Vector3Int", "float2", "float3", "float4", "int2",
               "int3", "int4", "long2", "sincos", "Plane"}


def cat_managed(decl, enum_names, delegate_names):
    """Category from a dump.cs C# type name. Its job is to split enums out of ints."""
    t = decl.strip()
    bare = t.split("<")[0].split(".")[-1].rstrip("?[]*")
    if bare == "void":
        return "v"
    if bare == "bool":
        return "b"
    if bare in ("float", "double", "decimal"):
        return "f"
    if bare == "string":
        return "s"
    if t.endswith("[]"):
        return "a"
    if bare in enum_names:
        return "e"
    if bare in delegate_names or DELEGATE_RE.search(t):
        return "d"
    if bare in ("int", "long", "short", "byte", "uint", "ulong", "ushort", "sbyte",
                "char", "Int16", "Int32", "Int64"):
        return "i"
    if bare in ("IntPtr", "UIntPtr"):
        return "p"
    if bare in VALUE_TYPES:
        return "t"
    if bare == "object" or "<" in t or bare in ("List", "Dictionary", "IList",
                                                "ICollection", "IEnumerable"):
        return "o"
    return "?"


def merge_cat(native, managed):
    """Native is authoritative for the wire; the managed view only refines it."""
    if native == "i" and managed == "e":
        return "e"
    if native == "t" and managed == "o":
        return "t"
    if native == "?" and managed in CAT_NAME:
        return managed
    return native


def fmt_of(ret_cat, cats):
    return "%s T(%s)" % (CAT_NAME.get(ret_cat, "?"), ",".join(CAT_NAME[c] for c in cats))


def parse_sig(sig):
    """native Signature -> (has_this, ret_token, [param tokens, no methodInfo])."""
    head, _, rest = sig.partition("(")
    body = rest.rsplit(")", 1)[0]
    raw = split_params(body)
    toks = [norm_type(p) for p in raw]
    has_this = bool(raw) and raw[0].endswith("__this")
    if has_this:
        toks = toks[1:]
    if toks and toks[-1] == "const MethodInfo*":
        toks = toks[:-1]
    return has_this, norm_type(head.strip()), toks


# ------------------------------------------------------------------ body scanning
REX_PREFIX = frozenset(range(0x48, 0x50))
CALL_RE = re.compile(b"[\xE8\xE9]")
PADDING_RE = re.compile(b"\xCC\xCC\xCC\xCC")
RIP_RE = re.compile(rb"[\x48-\x4C]?(?:\x8D|\x8B)[\x05\x0D\x15\x1D\x25\x2D\x35\x3D]")
GENERIC_STORE_RE = re.compile(rb"[\x88\x89\x00\x01\x08\x09\x20\x21\x28\x29\x30\x31]"
                              rb"[\x40-\xBF]")
IMM_STORE_RE = re.compile(rb"[\xC6\xC7\x80\x81\x83][\x40-\xBF]")
SSE_STORE_RE = re.compile(rb"(?:\xF3\x0F|\xF2\x0F|\x0F)\x11[\x40-\xBF]")


def iter_field_stores(body):
    """Yield displacements of writes into [reg+disp8|disp32].

    Deliberately skipped, because they are stack traffic rather than field
    traffic: mod=00 (no displacement, or rip-relative - rip is not a field),
    SIB with base rsp/rbp, and the bare [rbp+disp] form without REX.B.
    Also skipped: cmp/test forms, which read a field but never write it.
    """
    n = len(body)
    for rx, generic in ((SSE_STORE_RE, True), (GENERIC_STORE_RE, True),
                        (IMM_STORE_RE, False)):
        for m in rx.finditer(body):
            i = m.start()
            j = i
            rex = body[j - 1] if j and body[j - 1] in REX_PREFIX else 0
            if rx is SSE_STORE_RE:
                j = j + (2 if body[j] in (0xF3, 0xF2) else 0) + 2
            elif not generic:
                modrm = body[j + 1]
                reg = (modrm >> 3) & 7
                op = body[j]
                if op in (0xC6, 0xC7):
                    if reg != 0:         # /1..7 of C6/C7 are x87, not mov imm
                        continue
                elif reg == 7:
                    continue             # /7 of 80/81/83 is cmp: it reads, never writes
                j += 1
            else:
                j += 1
            if j >= n:
                continue
            modrm = body[j]
            mod, rm = modrm >> 6, modrm & 7
            if mod == 0:
                continue
            k = j + 1
            if rm == 4:
                if k >= n:
                    continue
                sib = body[k]
                if (sib & 7) in (4, 5) or ((sib >> 3) & 7) == 4:
                    continue
                k += 1
            elif rm == 5 and not rex & 1:
                continue
            if mod == 1:
                if k < n:
                    yield body[k]
            elif k + 4 <= n:
                yield struct.unpack_from("<I", body, k)[0]


def iter_rip_refs(body):
    """Yield (instruction_end, displacement) for lea/mov through [rip+disp32]."""
    n = len(body)
    for m in RIP_RE.finditer(body):
        i = m.start()
        j = i + (1 if body[i] in REX_PREFIX else 0)
        k = j + 2                        # opcode + modrm
        if k + 4 <= n:
            yield k + 4, struct.unpack_from("<i", body, k)[0]


class Native:
    """Random access to method bodies, call targets and metadata references."""

    def __init__(self, methods, metadata, strings, data, rva2off):
        self.methods, self.data, self.rva2off = methods, data, rva2off
        self.byaddr = {}
        for m in methods:
            if m["Address"]:
                self.byaddr.setdefault(m["Address"], m["Name"])
        self.addrs = sorted(self.byaddr)
        self.meta = {m["Address"]: m["Name"] for m in metadata}
        self.strs = {m["Address"]: m["Value"] for m in strings}

    def body(self, addr):
        if not addr:
            return None, 0
        o = self.rva2off(addr)
        if o is None:
            return None, 0
        i = bisect.bisect_right(self.addrs, addr)
        end = self.addrs[i] if i < len(self.addrs) else addr + 4096
        size = min(end - addr, BODY_SIZE_CAP)
        pad = PADDING_RE.search(self.data, o, o + size)
        if pad:
            # il2cpp pads every method body with int3, so the first padding run
            # is the true end; the gap to the next method is only an upper bound.
            size = pad.start() - o
        return self.data[o:o + size], size

    def calls(self, addr, body):
        """[(offset, 'Class$$Method')] for direct call/jmp targets that resolve."""
        out = []
        for m in CALL_RE.finditer(body):
            k = m.start()
            if k + 5 > len(body):
                continue
            rel = struct.unpack_from("<i", body, k + 1)[0]
            tgt = addr + k + 5 + rel
            i = bisect.bisect_right(self.addrs, tgt) - 1
            if i < 0:
                continue
            a = self.addrs[i]
            if a != addr and tgt - a < CALL_NEAR:
                out.append((k, self.byaddr[a]))
        return out

    def rip_names(self, addr, body):
        """TypeInfos and string literals reached through [rip+disp32]."""
        types, strs = [], []
        for end, disp in iter_rip_refs(body):
            tgt = addr + end + disp
            nm = self.meta.get(tgt)
            if nm is not None:
                types.append(nm)
            elif tgt in self.strs:
                strs.append(self.strs[tgt])
        return types, strs


# ----------------------------------------------------------------------- records
def split_name(name):
    i = name.find("$$")
    return (name[:i], name[i + 2:]) if i >= 0 else (name, "")


ARITY_RE = re.compile(r"^(.*?)`\d+")


def open_name(tok):
    """The name of the type a token names, with the generic part removed.

    script.json writes an instantiation as `List`1<Vector3>` and dump.cs writes
    the same type as the open declaration `List<T>`; a nested type is `A+B` in
    dump.cs and `A.B` in script.json. `InitBoard.<Update>d__12` keeps its
    name: the angle brackets there are a compiler-generated member, not a
    parameter list, and so are the ones closing an anonymous type's.
    """
    m = ARITY_RE.match(tok)
    if m:
        tok = m.group(1)
    tok = tok.replace("+", ".").replace("/", ".")
    i = tok.find("<")
    if i > 1 and tok[i - 1] != ".":
        tok = tok[:i]
    return tok.rstrip(".")


def type_index(types):
    """open type name -> the dump.cs types carrying it.

    Indexed twice over: by the fully qualified open name, and by the open name
    with the namespace dropped, because dump.cs writes a nested type as
    `Outer.Inner` under its namespace line while script.json spells it
    `Namespace.Outer.Inner`.
    """
    idx = collections.defaultdict(set)
    for fq, t in types.items():
        idx[open_name(fq)].add(fq)
        idx[open_name(t["name"])].add(fq)
    return idx


def resolve_types(cls, types, by_name):
    """script.json's class token -> every dump.cs type it can name.

    The whole token is tried first, then the leading `Namespace.` and `Outer.`
    pieces are dropped one at a time; the first step that names something wins.
    A type spelled exactly like the token beats one that only matches once the
    namespace was dropped, because the game declares its own `Lawnf` next to
    `Core.Lawnf`. What is left has to fit what was dropped -- the dump.cs
    namespace or outer type still has to be there -- and a step that fits
    nothing gives the next, shorter suffix a chance: dump.cs spells a deeply
    nested engine type as `Arm.Neon`, so the token's own namespace never
    appears in it. More than one type still standing is a real tie that only
    the body address can break.
    """
    alt = open_name(cls)
    parts = alt.split(".")
    for i in range(len(parts)):
        cand = ".".join(parts[i:])
        hits = by_name.get(cand)
        if not hits:
            continue
        exact = [fq for fq in hits if open_name(fq) == cand]
        if exact:
            return exact
        prefix = ".".join(parts[:i])
        fits = [fq for fq in hits                # i == 0 dropped nothing
                if not prefix or types[fq]["ns"] == prefix
                or fq.startswith(prefix + ".")]
        if fits:
            return fits
    return []


def pick_type(cands, addr, by_rva):
    """The one dump.cs type, using dump.cs's own `// RVA:` as the proof."""
    if len(cands) == 1:
        return cands[0]
    if not cands or not addr:
        return None
    hits = [fq for fq in cands if fq in by_rva.get(addr, ())]
    return hits[0] if len(hits) == 1 else None


def collect_names(types):
    enum_names = {n for n, t in types.items() if t["kind"] == "enum"}
    enum_names |= {n.rsplit(".", 1)[-1] for n in enum_names}
    delegate_names = {n for n, t in types.items()
                      if DELEGATE_RE.search(t["base"] or "") or n.endswith("Delegate")}
    delegate_names |= {n.rsplit(".", 1)[-1] for n in delegate_names}
    return enum_names, delegate_names


WORLD_FIELDS = ("Board", "Plant", "Zombie", "GameConfig")


def world_field_sets(types, by_name):
    """-> (every instance-field offset of each world class, the offsets it owns alone)

    `full` answers "did this body write a field of the class it belongs to", and
    for that the whole inherited layout is right. `only` answers "did it write a
    Board field", and there the layout has to be stripped of what the other
    classes also declare: Plant and Zombie both extend Entity, so most of their
    offsets are the same numbers, and an unstripped bit fires on 16% of property
    getters, which is noise, not signal.
    """
    full = {}
    for cls in WORLD_FIELDS:
        offs, seen, cur = set(), set(), cls
        while cur and cur not in seen:
            seen.add(cur)
            cands = resolve_types(cur, types, by_name)
            if len(cands) != 1:
                break                      # left the game's own types: engine fields
                                           # are header noise, not world state
            tfq = cands[0]
            offs |= set(types[tfq]["fields"].values())
            cur = types[tfq]["base"].split(",")[0].strip() or None
        full[cls] = offs
    only = {}
    for cls in WORLD_FIELDS:
        rest = set()
        for other in WORLD_FIELDS:
            if other != cls:
                rest |= full[other]
        only[cls] = full[cls] - rest
    return full, only


def scan_body(rec, nat, addr, field_sets, world_only):
    """Fill the body features of one game-image record."""
    body, size = nat.body(addr)
    if body is None:
        return
    fx = 0
    disps = {d for d in iter_field_stores(body) if d <= 0x2000}
    if disps:
        fx |= 1 << 13
    for name, bit in (("Board", 1 << 0), ("Plant", 1 << 1), ("Zombie", 1 << 2),
                      ("GameConfig", 1 << 15)):
        if disps & world_only[name]:
            fx |= bit
    if rec["cls"] in field_sets and disps & field_sets[rec["cls"]]:
        fx |= 1 << 3
    calls = nat.calls(addr, body)
    names = [n for _, n in calls]
    callee_cls = {split_name(n)[0] for n in names}
    if callee_cls & WORLD_CLASSES:
        fx |= 1 << 14
    if any(n.startswith(d) for n in names for d in RANDOM_DOORS):
        fx |= 1 << 4
    if any(n.startswith(d) for n in names for d in INSTANTIATE_DOORS):
        fx |= 1 << 5
    if any(n.startswith(d) for n in names for d in DESTROY_DOORS):
        fx |= 1 << 16
    if any(n.startswith(d) for n in names for d in ASSET_DOORS):
        fx |= 1 << 8
    if any(n.startswith(d) for n in names for d in SCENE_DOORS):
        fx |= 1 << 10
    if any(n.startswith(d) for n in names for d in SOUND_DOORS):
        fx |= 1 << 11
    if any(n.startswith(d) for n in names for d in COROUTINE_DOORS):
        fx |= 1 << 12
    if any(n.startswith(d) for n in names for d in EVENT_DOORS) or \
            any(EVENT_INVOKE_RE.search(n) for n in names):
        fx |= 1 << 6
    refs, strs = nat.rip_names(addr, body)
    if "GameAPP_TypeInfo" in set(refs):
        fx |= 1 << 7
    if strs:
        fx |= 1 << 9
    rec["fx"] = fx
    rec["sz"] = size
    rec["ncall"] = len(calls)
    rec["callee_cls"] = sorted(callee_cls)[:MAX_LIST]
    rec["refs"] = sorted(t[:-9] for t in dict.fromkeys(refs)
                         if t.endswith("_TypeInfo"))[:MAX_LIST]
    rec["strings"] = list(dict.fromkeys(strs))[:16]
    if rec["flags"] & (1 << 4) or fx & ((1 << 5) | (1 << 14) | (1 << 16)):
        rec["callees"] = names[:MAX_LIST]


def effect_of(flags, fx, ncall):
    """One word for what a method is. Priority order, first match wins:

    gen      compiler-generated holder, not game logic
    frame    per-frame or raw input callback: never re-sent over the wire
    blocked  a parameter cannot cross the wire (delegate/pointer/shared generic)
    world    writes Board/Plant/Zombie state, destroys an entity, or calls
             into a world class
    config   stores a field that only GameConfig declares
    side     instantiates, loads an asset, raises an event, plays a sound
    pure     no field store and no resolved call
    read     calls something but no field write was seen
    """
    if flags & (1 << 7):
        return "gen"
    if flags & (1 << 6):
        return "frame"
    if flags & (1 << 8):
        return "blocked"
    if fx & ((1 << 0) | (1 << 1) | (1 << 2) | (1 << 3) | (1 << 14) | (1 << 16)):
        return "world"
    if fx & (1 << 15):
        return "config"
    if fx & ((1 << 5) | (1 << 6) | (1 << 8) | (1 << 11) | (1 << 12)):
        return "side"
    if not (fx & (1 << 13)) and not ncall:
        return "pure"
    return "read"


def build(ctx, scan_bodies=True):
    nat, types = ctx["nat"], ctx["types"]
    by_name, by_rva = ctx["by_name"], ctx["by_rva"]
    unresolved = ctx.setdefault("unresolved", {})
    enum_names, delegate_names = ctx["enums"], ctx["delegates"]
    field_sets = ctx["field_sets"]
    by_managed = collections.defaultdict(list)
    for tname, t in types.items():
        for m in t["methods"]:
            by_managed[(tname, m["name"])].append(m)
    recs = []
    for sm in nat.methods:
        addr = sm["Address"]
        cls, meth = split_name(sm["Name"])
        sig = sm.get("Signature") or ""
        _, ret_tok, param_toks = parse_sig(sig) if sig else ("", "", [])
        cands = resolve_types(cls, types, by_name)
        tfq = pick_type(cands, addr, by_rva)
        managed = by_managed.get((tfq, open_name(meth))) if tfq else None
        managed = managed or []
        match = None
        if len(managed) == 1:
            match = managed[0]
        elif len(managed) > 1:
            same = [c for c in managed if c["addr"] == addr]
            if not same:
                same = [c for c in managed
                        if len(c["params"]) == len(param_toks)]
            if len(same) == 1:
                match = same[0]
        cats = []
        for i, p in enumerate(param_toks):
            nc = cat_native(p, enum_names, delegate_names)
            mc = "?"
            if match and i < len(match["params"]):
                mc = cat_managed(match["params"][i][0], enum_names, delegate_names)
            cats.append(merge_cat(nc, mc))
        ret_cat = cat_native(ret_tok, enum_names, delegate_names)
        if match:
            mr = cat_managed(match["ret"], enum_names, delegate_names)
            if ret_cat in ("?", "i") and mr == "e":
                ret_cat = "e"
            elif ret_cat == "?" and mr in CAT_NAME:
                ret_cat = mr
        flags = 0
        t = types.get(tfq) if tfq else None
        if t is None:
            unresolved[cls] = unresolved.get(cls, 0) + 1
        img = t["image"] if t else None
        if img in GAME_IMAGES:
            flags |= 1 << 3
        if not addr:
            flags |= 1 << 16
        if any(c in HARD_CATS for c in cats):
            flags |= 1 << 8
        for c, bit in (("a", 1 << 9), ("t", 1 << 10), ("o", 1 << 11),
                       ("e", 1 << 12), ("s", 1 << 13)):
            if c in cats:
                flags |= bit
        if meth.startswith(ACCESSOR_PREFIXES):
            flags |= 1 << 5
        if meth.startswith(FRAME_PREFIXES) or (meth in UNITY_LIFECYCLE
                                               and tfq in ctx["mono"]):
            flags |= 1 << 6
        if GENERATED_RE.search(cls) or GENERATED_RE.search(meth):
            flags |= 1 << 7
        if match:
            flags |= 1 << 17
            if match["static"]:
                flags |= 1 << 0
            if match["virtual"]:
                flags |= 1 << 15
            if "public" in match["mods"]:
                flags |= 1 << 1
            if re.search(r"\b(private|internal|protected)\b", match["mods"]):
                flags |= 1 << 2
            if meth in (".ctor", ".cctor"):
                flags |= 1 << 14
        elif len(managed) > 1:
            flags |= 1 << 18
        if (flags & (1 << 3)) and meth.startswith(VERB_PREFIXES) and \
                not flags & (1 << 5) and not flags & (1 << 6) and not flags & (1 << 7):
            flags |= 1 << 4
        rec = {"id": sm["Name"], "cls": cls, "m": meth, "rva": addr, "image": img,
               "fmt": fmt_of(ret_cat, cats), "cats": "".join(cats), "ret": ret_cat,
               "flags": flags, "fx": 0, "ncall": 0}
        if match:
            rec["cs"] = "%s %s(%s)" % (match["ret"], match["name"],
                                       ", ".join("%s %s" % (ty, nm)
                                                 for ty, nm in match["params"]))
            if match["rva"] not in (None, "-1"):
                chk = ctx.setdefault("rva_check", {"agree": 0, "generic": 0,
                                                   "disagree": []})
                if int(match["rva"], 16) == addr:
                    chk["agree"] += 1
                elif INSTANTIATION_RE.search(sm["Name"]):
                    # script.json names one instantiation, dump.cs declares the
                    # open generic once: many tokens, one body, so the two
                    # addresses legitimately differ. Not evidence of a bad join.
                    chk["generic"] += 1
                elif len(chk["disagree"]) < 20:
                    chk["disagree"].append("%s dump=%s script=0x%X"
                                           % (sm["Name"], match["rva"], addr))
        if scan_bodies and (flags & (1 << 3)):
            scan_body(rec, nat, addr, field_sets, ctx["world_only"])
        rec["eff"] = effect_of(rec["flags"], rec["fx"], rec["ncall"])
        recs.append(rec)
    return recs


def method_rva_index(types):
    """body address -> the dump.cs types declaring a method at that address.

    Two assemblies can declare the same type name, and script.json gives no
    hint which one a token means; the body address is the only evidence that
    separates them, and dump.cs prints it as `// RVA:` on every declaration.
    """
    idx = collections.defaultdict(set)
    for t in types.values():
        for m in t["methods"]:
            if m["addr"]:
                idx[m["addr"]].add(t["fq"])
    return idx


def mono_types(types):
    """dump.cs types Unity itself drives: MonoBehaviour and everything under it.

    `Start` on one of those is the engine's callback; `Start` on a plain class is
    a command someone issued, and the difference decides whether it may travel.
    """
    kids = collections.defaultdict(set)
    for fq, t in types.items():
        b = (t["base"] or "").split(",")[0].strip()
        if b:
            kids[b].add(fq)
    out, seen, queue = set(), set(MONO_ROOTS), collections.deque(MONO_ROOTS)
    while queue:
        for fq in kids.get(queue.popleft(), ()):
            out.add(fq)
            for key in (fq, fq.rsplit(".", 1)[-1]):
                if key not in seen:
                    seen.add(key)
                    queue.append(key)
    return out


def load_ctx():
    data, rva2off = load_pe()
    methods, metadata, strings = load_script()
    types, images = parse_dump_cs()
    by_name = type_index(types)
    enums, delegates = collect_names(types)
    nat = Native(methods, metadata, strings, data, rva2off)
    field_sets, world_only = world_field_sets(types, by_name)
    return {"nat": nat, "types": types, "images": images, "by_name": by_name,
            "by_rva": method_rva_index(types),
            "enums": enums, "delegates": delegates,
            "field_sets": field_sets, "world_only": world_only,
            "mono": mono_types(types)}


# ----------------------------------------------------------------------- commands
def cmd_types():
    methods, _, _ = load_script()
    toks = collections.Counter()
    for m in methods:
        _, ret, params = parse_sig(m.get("Signature") or "")
        toks[ret] += 1
        for p in params:
            toks[p] += 1
    show("=== distinct normalized type tokens: %d" % len(toks))
    for t, n in toks.most_common(150):
        show("%8d  %s" % (n, t))


def cmd_sig(sub):
    methods, _, _ = load_script()
    hits = [m for m in methods if sub in m["Name"]]
    show("=== %d method(s) match %s" % (len(hits), sub))
    for m in hits[:40]:
        show("%s  rva=0x%X  TypeSig=%s\n    %s"
             % (m["Name"], m["Address"], m["TypeSignature"], m["Signature"]))


def report_reconcile(recs, ctx, game, verbs):
    """Why the remembered numbers are larger than this index's, in measurements.

    Two conventions were in force when the baseline was first counted and both
    are gone now: methods were collected by the LAST name component of their
    class, whatever namespace that class really lives in, and the instance
    pointer was printed as the first parameter of the signature format.
    """
    types = ctx["types"]
    show("")
    show("=== is the game image fully attributed?")
    decls = {m["addr"] for t in types.values() if t["image"] in GAME_IMAGES
             for m in t["methods"] if m["addr"]}
    have = {r["rva"] for r in recs if r["image"] in GAME_IMAGES}
    show("  dump.cs declares %d game-image method bodies, this index attributes "
         "%d of them (%d missing)" % (len(decls), len(decls & have),
                                      len(decls - have)))
    for addr in sorted(decls - have)[:5]:
        show("    missing 0x%X" % addr)
    show("  the index names %d game-image class tokens; script.json carries no "
         "image field, so every one is proven by a dump.cs RVA"
         % len({r["cls"] for r in game}))
    show("")
    show("=== what the old count would have been under the loose convention")
    leaf = {open_name(t["name"]).split(".")[-1] for t in types.values()
            if t["image"] in GAME_IMAGES}
    loose = [r for r in recs if open_name(r["cls"]).split(".")[-1] in leaf]
    show("  class whose LAST name is a game name, any namespace: %d methods / "
         "%d tokens  (index: %d / %d, baseline 31719 / 4909)"
         % (len(loose), len({r["cls"] for r in loose}), len(game),
            len({r["cls"] for r in game})))
    extra = [r for r in loose if r["image"] not in GAME_IMAGES]
    show("  the %d extra methods come from tokens this index will not call game "
         "code: %s"
         % (len(extra), ", ".join(
             "%s=%d" % kv for kv in collections.Counter(
                 r["image"] or "no dump.cs type" for r in extra).most_common(4))))
    show("  e.g. %s" % ", ".join(sorted({r["cls"] for r in extra} -
                                        {r["cls"] for r in game})[:3]))
    other = [t for t in types.values() if t["image"] not in GAME_IMAGES
             and t["image"] in FIRST_PARTY_OTHER]
    show("")
    show("=== the game is not one assembly")
    for img in sorted(FIRST_PARTY_OTHER):
        rows = [t for t in types.values() if t["image"] == img]
        n = sum(1 for r in recs if r["image"] == img)
        show("  %s: %d types (%s), %d methods in the index, %d of them verbs"
             % (img, len(rows),
                ", ".join(sorted(t["name"] for t in rows
                                 if not t["name"].startswith("<"))[:6]),
                n, sum(1 for r in recs if r["image"] == img
                       and r["flags"] & (1 << 4))))
    show("  the atlas calls only %s game code; %s above carries %d verbs, so "
         "nothing sendable is lost by that definition"
         % (", ".join(sorted(GAME_IMAGES)), ", ".join(sorted(FIRST_PARTY_OTHER)),
            sum(1 for r in recs if r["image"] in FIRST_PARTY_OTHER
                and r["flags"] & (1 << 4))))
    show("")
    show("=== verb formats with the instance pointer counted as a parameter")
    fc = collections.Counter(
        fmt_of(r["ret"],
               ("" if r["flags"] & (1 << 0) else "o") + r["cats"])
        for r in verbs)
    for f, n in fc.most_common(3):
        show("  %5.1f%% %5d  %s" % (100.0 * n / max(1, len(verbs)), n, f))
    show("  (baseline said 36.9 / 15.9 / 8.6 for this family; without __this "
         "the same verbs read %s)"
         % " / ".join("%.1f" % (100.0 * n / max(1, len(verbs)))
                      for _, n in collections.Counter(
                          r["fmt"] for r in verbs).most_common(3)))


def report_counts(recs, ctx):
    game = [r for r in recs if r["flags"] & (1 << 3)]
    verbs = [r for r in game if r["flags"] & (1 << 4)]
    types, images = ctx["types"], ctx["images"]
    show("")
    show("=== per-image method counts")
    per_image = collections.Counter(r["image"] or "?" for r in recs)
    per_type = collections.defaultdict(set)
    for r in recs:
        if r["image"]:
            per_type[r["image"]].add(r["cls"])
    for img, n in sorted(per_image.items(), key=lambda kv: -kv[1])[:12]:
        show("  %-42s methods %6d  classes %5d" % (img, n, len(per_type[img])))
    show("  types declared in dump.cs: %d (image 0: %d)"
         % (len(types), sum(1 for t in types.values() if t["image"] in GAME_IMAGES)))
    show("")
    show("=== baseline comparison")
    got = {
        "methods_with_body": sum(1 for r in recs if r["rva"]),
        "game_methods": len(game),
        "game_types": len({r["cls"] for r in game}),
        "verbs": len(verbs),
        "verb_formats": len({r["fmt"] for r in verbs}),
        "verbs_hard_param": sum(1 for r in verbs if r["flags"] & (1 << 8)),
        "param_obj": sum(r["cats"].count("o") for r in verbs),
        "param_int": sum(r["cats"].count("i") + r["cats"].count("e") for r in verbs),
        "param_float": sum(r["cats"].count("f") for r in verbs),
        "param_bool": sum(r["cats"].count("b") for r in verbs),
        "param_string": sum(r["cats"].count("s") for r in verbs),
        "param_array": sum(r["cats"].count("a") for r in verbs),
        "frame_callbacks": sum(1 for r in game if r["flags"] & (1 << 6)),
        "accessors": sum(1 for r in game if r["flags"] & (1 << 5)),
        "generated": sum(1 for r in game if r["flags"] & (1 << 7)),
    }
    for k, base in BASELINE.items():
        g = got[k]
        show("  %-19s baseline %8d  index %8d  %s"
             % (k, base, g, "match" if g == base else "DIFF %s%d"
                % ("+" if g > base else "", g - base)))
    show("  (frame/accessor/generated counted over all images: %d / %d / %d)"
         % (sum(1 for r in recs if r["flags"] & (1 << 6)),
            sum(1 for r in recs if r["flags"] & (1 << 5)),
            sum(1 for r in recs if r["flags"] & (1 << 7))))
    show("")
    show("=== verb parameters by category")
    pc = collections.Counter()
    for r in verbs:
        pc.update(r["cats"])
    for c, n in sorted(pc.items(), key=lambda kv: -kv[1]):
        show("  %-3s %-6s %6d" % (c, CAT_NAME.get(c, "?"), n))
    show("  verbs with no parameters: %d" % sum(1 for r in verbs if not r["cats"]))
    show("")
    show("=== top signature formats among verbs")
    fc = collections.Counter(r["fmt"] for r in verbs)
    for f, n in fc.most_common(20):
        show("  %5.1f%% %5d  %s" % (100.0 * n / len(verbs), n, f))
    for top in (8, 20, 30):
        share = sum(n for _, n in fc.most_common(top))
        show("  cumulative %2d formats: %.1f%%" % (top, 100.0 * share / len(verbs)))
    show("")
    show("=== effect of game-image methods")
    for e, n in collections.Counter(r["eff"] for r in game).most_common():
        show("  %-9s %6d" % (e, n))
    show("  verbs by effect: %s" % ", ".join(
        "%s=%d" % (e, n) for e, n in collections.Counter(r["eff"] for r in verbs).most_common()))
    show("")
    show("=== body feature hits (game-image methods)")
    for name, bit in FX_TABLE:
        show("  %-16s %6d" % (name, sum(1 for r in game if r["fx"] & bit)))
    show("")
    show("=== managed (dump.cs) join quality")
    show("  matched             %6d" % sum(1 for r in recs if r["flags"] & (1 << 17)))
    show("  overload ambiguous  %6d" % sum(1 for r in recs if r["flags"] & (1 << 18)))
    show("  no managed decl     %6d" % sum(1 for r in recs
                                          if not r["flags"] & ((1 << 17) | (1 << 18))))
    chk = ctx.get("rva_check")
    if chk:
        show("  dump.cs RVA agrees with script.json Address: %d, generic "
             "instantiations skipped: %d, real disagreements: %d %s"
             % (chk["agree"], chk["generic"], len(chk["disagree"]),
                chk["disagree"][:5]))
    unres = ctx.get("unresolved") or {}
    show("  class tokens with no dump.cs type: %d distinct, %d methods"
         % (len(unres), sum(unres.values())))
    for cls, n in sorted(unres.items(), key=lambda kv: -kv[1])[:15]:
        show("    %6d  %s" % (n, cls[:76]))
    report_reconcile(recs, ctx, game, verbs)


def cmd_stats():
    ctx = load_ctx()
    t0 = time.time()
    recs = build(ctx)
    show("built %d records in %.1fs" % (len(recs), time.time() - t0))
    report_counts(recs, ctx)


def cmd_calibrate():
    ctx = load_ctx()
    recs = build(ctx)
    game = [r for r in recs if r["flags"] & (1 << 3)]
    show("=== noise floor: a field getter has no business writing anything")
    show("    (bodies <= 32 bytes: a real state change needs several stores and")
    show("     the calls that compute them, so a hit here is a false positive)")
    getters = [r for r in game if r["m"].startswith("get_")
               and r.get("sz", 9999) <= 32]
    ctors = [r for r in game if r["m"] == ".ctor" and r.get("sz", 9999) <= 32]
    for name, bit in FX_TABLE:
        if not name.startswith(("wr_", "store_any")):
            continue
        g = sum(1 for r in getters if r["fx"] & bit)
        c = sum(1 for r in ctors if r["fx"] & bit)
        show("  %-10s accessors %4d/%4d (%.1f%%)   ctors %4d/%4d (%.1f%%)"
             % (name, g, len(getters), 100.0 * g / max(1, len(getters)),
                c, len(ctors), 100.0 * c / max(1, len(ctors))))
    show("  population: %d small accessors, %d small ctors of %d game methods"
         % (len(getters), len(ctors), len(game)))
    show("")
    show("=== known-answer checks from ref/EVIDENCE.md")
    checks = ("Board$$GetSun", "Board$$SetSun", "CoinSun$$Die", "Board$$SunUpdate",
              "CreateItem$$SetCoin", "DifficultyMgr$$Update", "Mouse$$TryToSetPlantByCard",
              "Plant$$Die", "Board$$ClearTheBoard", "SaveMgr$$CaptureBoard",
              "InitBoard$$AddCard", "CardSlotManager$$AddCard", "DrawCardManager$$Pull")
    for mid in checks:
        r = next((x for x in recs if x["id"] == mid), None)
        if not r:
            show("  %-34s NOT IN INDEX" % mid)
            continue
        show("  %-34s rva=0x%-8X sz=%-5s ncall=%-3s eff=%-7s %s"
             % (mid, r["rva"], r.get("sz", "-"), r.get("ncall", "-"), r["eff"],
                ",".join(fx_names(r["fx"])) or "-"))
        if "callees" in r:
            show("      callees: %s" % ", ".join(r["callees"][:12]))
        if r.get("strings"):
            show("      strings: %s" % ", ".join(s[:40] for s in r["strings"][:6]))
    show("")
    show("=== call resolution yield")
    raw = resolved = 0
    for r in game:
        body, _ = ctx["nat"].body(r["rva"])
        if body:
            raw += len(CALL_RE.findall(body))
            resolved += r["ncall"]
    show("  E8/E9 byte positions %d, resolved call targets %d (%.0f%% of positions)"
         % (raw, resolved, 100.0 * resolved / max(1, raw)))


def cmd_class(classes):
    ctx = load_ctx()
    recs = build(ctx)
    types = ctx["types"]
    for cls in classes:
        sel = [r for r in recs if r["cls"] == cls]
        show("=== class %s: %d method(s)" % (cls, len(sel)))
        show("%-36s %-10s %-26s %-8s %s" % ("method", "rva", "format", "effect", "features"))
        for r in sorted(sel, key=lambda x: x["m"]):
            show("%-36s %-10s %-26s %-8s %s"
                 % (r["m"], hex(r["rva"]) if r["rva"] else "-", r["fmt"], r["eff"],
                    ",".join(fx_names(r["fx"])) or "-"))
            if r.get("cs"):
                show("      managed: %s" % r["cs"])
        t = types.get(cls)
        if t is None:
            cands = resolve_types(cls, types, ctx["by_name"])
            t = types[cands[0]] if len(cands) == 1 else None
        if t:
            show("--- fields of %s: %d instance, %d static (image %s, base %s)"
                 % (cls, len(t["fields"]), len(t["statics"]), t["image"], t["base"]))
            for n, o in sorted(t["fields"].items(), key=lambda kv: kv[1]):
                show("    0x%-6X %s" % (o, n))
        show("")


def cmd_method(mid):
    ctx = load_ctx()
    if "@" in mid:
        cls, m = mid.split("@", 1)
        mid = "%s$$%s" % (cls, m)
    recs = build(ctx)
    r = next((x for x in recs if x["id"] == mid), None)
    if not r:
        show("no record for %s" % mid)
        return
    show(json.dumps(r, indent=1)[:9000])
    body, size = ctx["nat"].body(r["rva"])
    show("--- body: %d bytes, %d resolved calls" % (size, len(ctx["nat"].calls(r["rva"], body))))
    for k, n in ctx["nat"].calls(r["rva"], body):
        show("  +0x%03X -> %s" % (k, n))
    refs, strs = ctx["nat"].rip_names(r["rva"], body)
    show("--- rip refs")
    for n in dict.fromkeys(refs):
        show("  ref %s" % n)
    for s in dict.fromkeys(strs):
        show("  str %s" % s)


def cmd_candidates(top=40):
    """Rows for the arbiter table: game classes with sendable verbs, most first."""
    ctx = load_ctx()
    recs = build(ctx)
    by_cls = collections.defaultdict(list)
    for r in recs:
        if r["flags"] & (1 << 4):
            by_cls[r["cls"]].append(r)
    show("=== arbiter candidates: verbs per class")
    show("%-26s %5s %5s %6s %6s %6s  %s"
         % ("class", "verbs", "send", "world", "side", "blocked", "sample methods"))
    ranked = sorted(by_cls.items(),
                    key=lambda kv: -sum(1 for r in kv[1] if r["flags"] & (1 << 8) == 0
                                        and r["eff"] in ("world", "side")))
    for cls, rows in ranked[:top]:
        send = [r for r in rows if not r["flags"] & (1 << 8) and r["eff"] == "world"]
        side = [r for r in rows if not r["flags"] & (1 << 8) and r["eff"] == "side"]
        blocked = sum(1 for r in rows if r["eff"] == "blocked")
        names = ", ".join(r["m"] for r in (send or side or rows)[:6])
        show("%-26s %5d %5d %6d %6d %6d  %s"
             % (cls, len(rows), len(send) + len(side), len(send), len(side),
                blocked, names[:70]))
    show("")
    show("=== verbs that are blocked by an unsendable parameter (top 25)")
    blocked = [r for r in recs if (r["flags"] & (1 << 4)) and r["eff"] == "blocked"]
    for r in blocked[:25]:
        show("  %-28s %-26s %s" % (r["cls"], r["m"], r["fmt"]))
    show("  total blocked verbs: %d" % len(blocked))


# ---- the coverage allow-list --------------------------------------------------
# The table is a decision, so it is written by hand in C#. The claims inside it (a
# name, a parameter count, what the body does) belong to the build, so they are
# checked here against GameAssembly.dll and emitted as measured facts next to them.
ROW_RE = re.compile(r'Rule\(\s*"([^"]*)",\s*"([^"]*)",\s*(\d+),\s*'
                    r'Authority\.(\w+),\s*Verdict\.(\w+)\s*\)')

UNSENDABLE = ("frame", "gen", "blocked")
IMMUTABLE = ("read", "pure")
# A body that reaches the engine's random source cannot be replayed: the same call on
# two machines gives two answers, so "the code travels" stops meaning "the world agrees".
RANDOM_CLASS = "UnityEngine.Random"

FACTS_HEADER = [
    "// Generated by tools/re/pvz-index.py --arbiter write. Do not edit this file.",
    "//",
    "// What GameAssembly.dll says about every row of PvzMP.Core.Session.Coverage: the address of",
    "// the body, its measured length, the calls out of it that resolve, the parameter categories,",
    "// the effect class and whether the body reaches UnityEngine.Random. A row that stopped matching",
    "// the build is refused by the generator, so the mod cannot keep sending a call the native method",
    "// no longer answers. Rerun after adding or changing a row:",
    "//",
    "//   cd /d/PvzMP && py -3 tools/re/pvz-index.py --arbiter write",
    ""]


def parse_rows(path):
    """The allow-list read back out of the C# it is written in."""
    with open(path, encoding="utf-8") as fh:
        text = fh.read()
    rows = []
    for cls, meth, arity, authority, verdict in ROW_RE.findall(text):
        rows.append((cls, meth, int(arity), authority, verdict))
    return rows


def body_rolls(rec):
    """True when the measured body resolves a call into UnityEngine.Random."""
    return RANDOM_CLASS in rec.get("callee_cls", [])


def cmd_arbiter(write=False):
    """Prove every coverage row against the build, then emit the facts it needs."""
    ctx = load_ctx()
    recs = build(ctx)
    rows = parse_rows(COVERAGE_CS)
    if not rows:
        show("no rows read from %s - the Rule(...) shape is fixed, check the file" % COVERAGE_CS)
        return 1

    problems, facts, seen = [], [], set()
    for cls, meth, arity, authority, verdict in rows:
        key = "%s.%s/%d" % (cls, meth, arity)
        if key in seen:
            problems.append("%s: the table names it twice" % key)
            continue
        seen.add(key)

        sel = [r for r in recs if r["cls"] == cls and r["m"] == meth
               and len(r["cats"]) == arity and r["flags"] & (1 << 3)]
        addrs = sorted({r["rva"] for r in sel})
        if not sel:
            have = sorted({len(r["cats"]) for r in recs
                           if r["cls"] == cls and r["m"] == meth})
            problems.append("%s: no such native method%s"
                            % (key, " (arities in the build: %s)" % have if have else ""))
            continue
        if len(addrs) > 1:
            problems.append("%s: two bodies answer it: %s"
                            % (key, ", ".join(hex(a) for a in addrs)))
            continue

        r = sel[0]
        eff = r["eff"]
        if eff in UNSENDABLE and verdict != "Never":
            problems.append("%s: the body is %s, so only Verdict.Never may name it" % (key, eff))
            continue
        if verdict == "Replicate" and eff in IMMUTABLE:
            problems.append("%s: Verdict.Replicate on a body that only %ss" % (key, eff))
            continue
        if verdict == "Replicate" and body_rolls(r):
            problems.append("%s: the body reaches %s, so replaying it would give every end "
                            "a different answer" % (key, RANDOM_CLASS))
            continue
        facts.append((key, r, authority, verdict))

    show("=== coverage table: %d row(s), %d measured against the build"
         % (len(rows), len(facts)))
    show("%-34s %-10s %-22s %-8s %-9s %-6s %s"
         % ("key", "rva", "format", "effect", "verdict", "rolls", "authority"))
    for key, r, authority, verdict in sorted(facts):
        show("%-34s %-10s %-22s %-8s %-9s %-6s %s"
             % (key, hex(r["rva"]), r["fmt"], r["eff"], verdict,
                "yes" if body_rolls(r) else "-", authority))
    for problem in problems:
        show("PROBLEM " + problem)
    show("")
    show("rows %d, measured %d, problems %d" % (len(rows), len(facts), len(problems)))
    if problems:
        show("the coverage table is rejected: fix the row or the index, then rerun")
        return 1

    if not write:
        show("nothing written: pass write to emit %s" % FACTS_CS)
        return 0

    out = list(FACTS_HEADER)
    out += ["namespace PvzMP.Core.Session", "{",
            "    public static class CoverageFacts", "    {",
            "        public static readonly CoverageFact[] Rows =", "        {"]
    for key, r, _authority, _verdict in sorted(facts):
        out.append('            F("%s", 0x%X, %d, %d, %s, "%s", "%s", "%s", %s),'
                   % (key, r["rva"], r.get("sz", 0), r["ncall"],
                      "true" if r["flags"] & (1 << 0) else "false",
                      r["fmt"], r["cats"], r["eff"],
                      "true" if body_rolls(r) else "false"))
    out += ["        };", "",
            "        private static CoverageFact F(string key, long rva, int size, int calls,",
            "                                      bool isStatic, string format, string cats,",
            "                                      string effect, bool rolls)",
            "        {",
            "            return new CoverageFact(key, rva, size, calls, isStatic, format, cats,",
            "                                    effect, rolls);",
            "        }",
            "    }", "}", ""]
    with open(FACTS_CS, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(out))
    show("wrote %s: %d fact row(s)" % (FACTS_CS, len(facts)))
    return 0


def cmd_write(only):
    ctx = load_ctx()
    t0 = time.time()
    recs = build(ctx)
    if only:
        recs = [r for r in recs if r["cls"] in set(only)]
    counts = collections.Counter()
    for r in recs:
        counts["records"] += 1
        counts["with_body"] += 1 if r["rva"] else 0
        counts["game"] += 1 if r["flags"] & (1 << 3) else 0
        counts["verb"] += 1 if r["flags"] & (1 << 4) else 0
        counts["eff_" + r["eff"]] += 1
    out = {"meta": {
        "generated_by": "tools/re/pvz-index.py",
        "build": "PvzFusion en-4.1, Unity 2022.3, il2cpp metadata 31",
        "sources": {"script_json": SCRIPT, "dump_cs": DUMPCS, "dll": DLL},
        "game_images": sorted(GAME_IMAGES),
        "legend": {"cats": CAT_NAME, "hard_cats": sorted(HARD_CATS),
                   "flags": dict(bit_names_table(FLAG_TABLE)),
                   "fx": dict(bit_names_table(FX_TABLE)),
                   "effect_rules": [ln.strip() for ln in
                                    effect_of.__doc__.splitlines() if ln.strip()],
                   "world_classes": sorted(WORLD_CLASSES)},
        "counts": dict(counts),
        "elapsed_sec": round(time.time() - t0, 1)},
        "types": {n: {"image": t["image"], "kind": t["kind"], "base": t["base"],
                      "tidx": t["tidx"], "fields": t["fields"],
                      "statics": t["statics"], "nmethods": len(t["methods"])}
                  for n, t in ctx["types"].items() if t["image"] in GAME_IMAGES},
        "methods": recs}
    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(out, fh, separators=(",", ":"))
    size_mb = open(OUT, "rb").seek(0, 2) / 1048576.0
    show("wrote %s" % OUT)
    show("  records %d, game %d, verbs %d, %.1fs, %.1f MB"
         % (counts["records"], counts["game"], counts["verb"],
            time.time() - t0, size_mb))


def bit_names_table(table):
    return [(n, b) for n, b in table]


if __name__ == "__main__":
    argv = sys.argv[1:]
    opt = argv[0] if argv else "--types"
    if opt == "--types":
        cmd_types()
    elif opt == "--sig":
        cmd_sig(argv[1])
    elif opt == "--stats":
        cmd_stats()
    elif opt == "--calibrate":
        cmd_calibrate()
    elif opt == "--class":
        cmd_class(argv[1:])
    elif opt == "--method":
        cmd_method(argv[1])
    elif opt == "--candidates":
        cmd_candidates(int(argv[1]) if len(argv) > 1 else 40)
    elif opt == "--arbiter":
        sys.exit(cmd_arbiter(len(argv) > 1 and argv[1] == "write"))
    elif opt == "--write":
        cmd_write(argv[1:])
    else:
        show("unknown option %s - see the module docstring" % opt)
