import json, struct, sys, bisect

DLL = r"D:\PvzFusion\GameAssembly.dll"
SCRIPT = r"D:\PvzMP\ref\dump-en\script.json"

# The answer to "can this door be skipped without hurting the game?".
#
# A Harmony prefix that returns false makes the native body never run, so an object-returning method
# hands its caller whatever the default is: a null pointer. The bodies that then read through that
# pointer do not throw a managed exception first - a field access is a plain address calculation - so
# the answer is not a property of the door, it is a property of every caller it has. This walks every
# static call site of the named method and reads what the caller does with rax, where the pointer
# arrives.
#
#   checked   a test or compare of rax comes first, so the caller looks at the answer
#   passed    rax is moved into an argument register, so the next call gets it as `this` or as an arg
#   deref     a memory operand based on rax, so a field or a virtual call reads through it
#   returned  the body ends (ret) before touching it
#   dropped   the next call/branch is reached with rax untouched, so the answer was discarded
#
# `passed` and `deref` are the ones that turn a skipped door into a crash, and they are printed as the
# dangerous list. Only the FIRST use within 96 bytes is classified: a caller that stores the pointer in
# a stack local and reads it back three hundred bytes later is not answered here, which is why the
# buckets are printed too and an empty `stored` bucket is part of the evidence.

data = open(DLL, "rb").read()
pe = struct.unpack_from("<I", data, 0x3C)[0]
nsec = struct.unpack_from("<H", data, pe + 6)[0]
opt = struct.unpack_from("<H", data, pe + 20)[0]
secs = []
off = pe + 24 + opt
for i in range(nsec):
    name = data[off:off + 8].rstrip(b"\0").decode()
    vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
    secs.append((vaddr, vsize, raddr, rsize, name))
    off += 40


def rva2off(rva):
    for vaddr, vsize, raddr, rsize, name in secs:
        if vaddr <= rva < vaddr + max(vsize, rsize):
            return raddr + (rva - vaddr)
    return None


methods = json.load(open(SCRIPT, encoding="utf-8"))["ScriptMethod"]
byaddr = {}
for m in methods:
    a = m["Address"]
    if a:
        byaddr.setdefault(a, m["Name"])
addrs = sorted(byaddr)

WINDOW = 96

TEST = {
    b"\x48\x85\xc0": "test rax,rax",
    b"\x48\x3d\x00\x00\x00\x00": "cmp rax,0",
}

# mov rcx/rdx/r8/r9, rax - the argument registers of the x64 calling convention
PASS = {
    b"\x48\x8b\xc8": "mov rcx,rax",
    b"\x48\x8b\xd0": "mov rdx,rax",
    b"\x49\x8b\xc0": "mov r8,rax",
    b"\x4c\x8b\xc8": "mov r9,rax",
}


def first_use(body, start):
    k = start
    lim = min(len(body), start + WINDOW)
    while k < lim:
        for pat, what in TEST.items():
            if body[k:k + len(pat)] == pat:
                return "checked", what
        for pat, what in PASS.items():
            if body[k:k + len(pat)] == pat:
                return "passed", what
        # call [rax+disp8] / call [rax+disp32]: a virtual call through the answer
        if body[k:k + 2] in (b"\xff\x50", b"\xff\x90"):
            return "deref", "call [rax+..]"
        # mov reg,[rax+disp] / mov [rax+disp],reg: a field read or write through the answer
        if body[k] in (0x8B, 0x89) and k + 1 < lim:
            modrm = body[k + 1]
            if (modrm >> 6) in (1, 2) and (modrm & 7) == 0:
                return "deref", "mov [rax+..]"
        if body[k] == 0xC3:
            return "returned", "ret"
        if body[k] in (0xE8, 0xE9) and k > start:
            return "dropped", "next call"
        if body[k:k + 3] == b"\x48\x89\x45":
            return "stored", "mov [rbp+x],rax"
        k += 1
    return "window", "nothing classified"


def body_of(a):
    i = bisect.bisect_right(addrs, a)
    end = addrs[i] if i < len(addrs) else a + 8192
    o = rva2off(a)
    if o is None:
        return None
    return data[o:o + min(end - a, 8192)]


def sites_of(target):
    found = []
    for m in methods:
        a = m["Address"]
        if not a:
            continue
        body = body_of(a)
        if body is None:
            continue
        for k in range(len(body) - 5):
            if body[k] not in (0xE8, 0xE9):
                continue
            rel = struct.unpack_from("<i", body, k + 1)[0]
            if a + k + 5 + rel == target:
                kind, what = first_use(body, k + 5)
                found.append((m["Name"], k, kind, what))
    return found


def show(text):
    sys.stdout.write(text.encode("ascii", "replace").decode("ascii"))


def report(name, only):
    hits = [m for m in methods if m["Name"] == name and m["Address"]]
    if not hits:
        hits = [m for m in methods if name in m["Name"] and m["Address"]]
    if not hits:
        show("=== %s: no method with that name has a body ===\n" % name)
        return
    for m in hits:
        target = m["Address"]
        found = sites_of(target)
        if only:
            found = [f for f in found if only in f[0]]
        buckets = {}
        for nm, k, kind, what in found:
            buckets.setdefault(kind, []).append((nm, k, what))

        show("=== %s  rva=0x%X  %d static call site(s) ===\n" % (m["Name"], target, len(found)))
        for kind in sorted(buckets):
            show("  %-9s %d\n" % (kind, len(buckets[kind])))

        bad = buckets.get("deref", []) + buckets.get("passed", [])
        if bad:
            show("  DANGEROUS (a skipped body hands these a null pointer):\n")
            for nm, k, what in sorted(bad):
                show("    %-70s +0x%X %s\n" % (nm, k, what))
        if not found:
            show("  (no static caller calls this method)\n")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    only = None
    for a in sys.argv[1:]:
        if a.startswith("--only"):
            only = a.split("=", 1)[1] if "=" in a else None
    for a in args:
        report(a, only)
