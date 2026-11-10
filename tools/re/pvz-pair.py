import json, struct, bisect

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

# float load:  F3 0F 10 <modrm=01xxx yyy> <disp8=src>   -> xmm[xxx] = [reg[yyy]+src]
# float store: F3 0F 11 <modrm=01xxx yyy> <disp8=dst>   -> [reg[yyy]+dst] = xmm[xxx]
def find(src, dst, window=32):
    hits = []
    n = len(data)
    for k in range(n - 5):
        if data[k] != 0xF3 or data[k + 1] != 0x0F or data[k + 2] != 0x10:
            continue
        modrm = data[k + 3]
        if (modrm & 0xC0) != 0x40 or (modrm & 0x07) == 0x04 or data[k + 4] != src:
            continue
        reg = (modrm >> 3) & 7
        for j in range(k + 5, min(k + 5 + window, n - 5)):
            if data[j] != 0xF3 or data[j + 1] != 0x0F or data[j + 2] != 0x11:
                continue
            smodrm = data[j + 3]
            if (smodrm & 0xC0) != 0x40 or (smodrm & 0x07) == 0x04 or data[j + 4] != dst:
                continue
            if (smodrm >> 3) & 7 == reg:
                hits.append((off2rva(k), off2rva(j)))
                break
    return hits

import sys
src = int(sys.argv[1], 16)
dst = int(sys.argv[2], 16)
hits = find(src, dst)
print("=== [x+0x%X] -> [x+0x%X] : %d sites" % (src, dst, len(hits)))
seen = set()
for a, b in hits:
    if a is None:
        continue
    o = owner(a)
    if o in seen:
        continue
    seen.add(o)
    print("   %s   (store at %s)" % (o, owner(b) if b else "?"))
