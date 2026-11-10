import json, struct, sys, bisect

DLL = r"D:\PvzFusion\GameAssembly.dll"
SCRIPT = r"D:\PvzMP\ref\dump-en\script.json"

data = open(DLL, "rb").read()
pe = struct.unpack_from("<I", data, 0x3C)[0]
nsec = struct.unpack_from("<H", data, pe + 6)[0]
opt = struct.unpack_from("<H", data, pe + 20)[0]
secs = []
off = pe + 24 + opt
for i in range(nsec):
    name = data[off:off+8].rstrip(b"\0").decode()
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
    if m["Address"]:
        byaddr.setdefault(m["Address"], m["Name"])
addrs = sorted(byaddr)

targets = {}
for part in sys.argv[1:]:
    for a, n in byaddr.items():
        if part in n:
            targets[a] = n

print("looking for %d target(s)" % len(targets))
for a, n in targets.items():
    print("   0x%X %s" % (a, n))

found = {}
for i, a in enumerate(addrs):
    o = rva2off(a)
    if o is None:
        continue
    end = addrs[i + 1] if i + 1 < len(addrs) else a + 4096
    size = min(end - a, 8192)
    body = data[o:o+size]
    for k in range(len(body) - 5):
        if body[k] in (0xE8, 0xE9):
            rel = struct.unpack_from("<i", body, k + 1)[0]
            tgt = a + k + 5 + rel
            if tgt in targets:
                found.setdefault(targets[tgt], []).append("%s+0x%X" % (byaddr[a], k))

def show(text):
    print(str(text).encode("ascii", "replace").decode("ascii"))

for n in sorted(found):
    show("=== callers of %s: %d" % (n, len(found[n])))
    for c in found[n][:60]:
        show("   " + c)
for n in sorted(set(targets.values()) - set(found)):
    show("=== %s : no static caller found" % n)