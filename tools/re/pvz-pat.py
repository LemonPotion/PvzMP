import json, struct, sys, bisect, re

DLL = r"D:\PvzFusion\GameAssembly.dll"
SCRIPT = r"D:\PvzMP\ref\dump-en\script.json"

data = open(DLL, "rb").read()
pe = struct.unpack_from("<I", data, 0x3C)[0]
nsec = struct.unpack_from("<H", data, pe + 6)[0]
opt = struct.unpack_from("<H", data, pe + 20)[0]
secs = []
off = pe + 24 + opt
for i in range(nsec):
    vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
    secs.append((vaddr, vsize, raddr, rsize))
    off += 40

def off2rva(o):
    for vaddr, vsize, raddr, rsize in secs:
        if raddr <= o < raddr + rsize:
            return vaddr + (o - raddr)
    return None

methods = json.load(open(SCRIPT, encoding="utf-8"))["ScriptMethod"]
byaddr = {}
for m in methods:
    if m["Address"]:
        byaddr.setdefault(m["Address"], m["Name"])
addrs = sorted(byaddr)

def owner(rva):
    i = bisect.bisect_right(addrs, rva) - 1
    return "?" if i < 0 else "%s+0x%X" % (byaddr[addrs[i]], rva - addrs[i])

# args: hex string with ".." as a wildcard byte, e.g. "F30F11....44", or
# "A|lo-hi|B" for a gap of lo..hi filler bytes between A and B.
# re.S matters: without it a wildcard never matches 0x0A, which is a ModRM byte
# (`mov rax,[rdx]`) and silently hides call sites.
def show(text):
    print(str(text).encode("ascii", "replace").decode("ascii"))

def tokens(spec):
    out = []
    i = 0
    while i < len(spec):
        if spec[i] == "{":
            j = spec.index("}", i)
            out.append(b".{" + spec[i + 1:j].encode("ascii") + b"}")
            i = j + 1
        elif spec[i:i + 2] == "..":
            out.append(b".")
            i += 2
        else:
            out.append(re.escape(bytes.fromhex(spec[i:i + 2])))
            i += 2
    return b"".join(out)

for arg in sys.argv[1:]:
    if "|" in arg:
        a, gap, b = arg.split("|")
        gap_lo, gap_hi = (gap.split("-") + [gap])[:2]
        spec = a + "{" + gap_lo + "," + gap_hi + "}" + b
    else:
        spec = arg
    pat = re.compile(tokens(spec), re.S)
    hits = [off2rva(m.start()) for m in pat.finditer(data)]
    hits = [h for h in hits if h is not None]
    show("=== /%s/: %d sites" % (arg, len(hits)))
    for h in hits[:200]:
        show("   0x%X %s" % (h, owner(h)))


