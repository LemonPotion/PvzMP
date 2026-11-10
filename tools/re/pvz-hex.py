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
    vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
    secs.append((vaddr, vsize, raddr, rsize))
    off += 40

def rva2off(rva):
    for vaddr, vsize, raddr, rsize in secs:
        if vaddr <= rva < vaddr + max(vsize, rsize):
            return raddr + (rva - vaddr)
    return None

methods = json.load(open(SCRIPT, encoding="utf-8"))["ScriptMethod"]
byaddr = {}
for m in methods:
    if m["Address"]:
        byaddr.setdefault(m["Address"], m["Name"])
addrs = sorted(byaddr)

def dump(namepart, maxbytes=4096):
    for m in methods:
        if namepart not in m["Name"] or not m["Address"]:
            continue
        a = m["Address"]
        i = bisect.bisect_right(addrs, a)
        end = addrs[i] if i < len(addrs) else a + 4096
        size = min(end - a, maxbytes)
        o = rva2off(a)
        if o is None:
            continue
        body = data[o:o+size]
        print("=== %s  rva=0x%X size=%d" % (m["Name"], a, size))
        for k in range(0, len(body), 16):
            chunk = body[k:k+16]
            print("  +0x%04X  %s" % (k, " ".join("%02X" % b for b in chunk)))

if __name__ == "__main__":
    for part in sys.argv[1:]:
        dump(part)