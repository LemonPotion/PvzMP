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
    if i < 0:
        return "?"
    return "%s+0x%X" % (byaddr[addrs[i]], rva - addrs[i])

# patterns: mov dword [reg+disp32], imm32  (C7 /0)  and mov byte [reg+disp32], imm8 (C6 /0)
def scan(disp, imm, width):
    d = struct.pack("<I", disp)
    if width == 4:
        pat = re.compile(b"\xC7[\x80-\x87]" + re.escape(d) + re.escape(struct.pack("<I", imm)))
    else:
        pat = re.compile(b"\xC6[\x80-\x87]" + re.escape(d) + re.escape(bytes([imm])))
    out = []
    for m in pat.finditer(data):
        r = off2rva(m.start())
        if r is not None:
            out.append(owner(r))
    return out

if __name__ == "__main__":
    for arg in sys.argv[1:]:
        disp, imm, width = arg.split(":")
        res = scan(int(disp, 16), int(imm, 0), int(width))
        print("=== write [reg+0x%s] = %s (%d bytes): %d sites" % (disp, imm, int(width), len(res)))
        for r in res[:80]:
            print("   " + r)