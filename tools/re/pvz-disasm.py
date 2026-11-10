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
    a = m["Address"]
    if a:
        byaddr.setdefault(a, m["Name"])
addrs = sorted(byaddr)

def lookup(rva):
    i = bisect.bisect_right(addrs, rva) - 1
    if i < 0:
        return None
    a = addrs[i]
    return a, byaddr[a], rva - a

def analyse(namepart, limit=40):
    hits = [m for m in methods if namepart in m["Name"] and m["Address"]]
    for m in hits[:limit]:
        a = m["Address"]
        i = bisect.bisect_right(addrs, a)
        end = addrs[i] if i < len(addrs) else a + 4096
        size = min(end - a, 8192)
        o = rva2off(a)
        if o is None:
            continue
        body = data[o:o+size]
        print("=== %s  rva=0x%X size=%d" % (m["Name"], a, size))
        seen = []
        for k in range(len(body) - 5):
            if body[k] in (0xE8, 0xE9):
                rel = struct.unpack_from("<i", body, k + 1)[0]
                tgt = a + k + 5 + rel
                r = lookup(tgt)
                if r and r[2] < 64 and r[0] != a:
                    seen.append((k, r[1]))
        for k, n in seen:
            print("   +0x%03X -> %s" % (k, n))
        if not seen:
            print("   (no resolved calls)")

if __name__ == "__main__":
    for part in sys.argv[1:]:
        analyse(part)