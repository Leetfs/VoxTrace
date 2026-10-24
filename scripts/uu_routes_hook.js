// Hook iphlpapi route APIs in UU and print each prefix as it is added.
// Usage: python scripts/uu_routes_frida.py [uu.exe|pid]

function ntohl(v) {
    return ((v & 255) << 24) |
        (((v >>> 8) & 255) << 16) |
        (((v >>> 16) & 255) << 8) |
        ((v >>> 24) & 255);
}

function ip(v) {
    v = ntohl(v) >>> 0;
    return [(v >>> 24) & 255, (v >>> 16) & 255, (v >>> 8) & 255, v & 255].join(".");
}

function maskBits(m) {
    m = ntohl(m) >>> 0;
    var bits = 0;
    for (var i = 31; i >= 0; i--) {
        if (m & (1 << i)) bits++;
        else break;
    }
    return bits;
}

function exportAddress(name) {
    try {
        if (Module.getGlobalExportByName) {
            return Module.getGlobalExportByName(name);
        }
    } catch (e) { }
    try {
        if (Module.getExportByName) {
            return Module.getExportByName("iphlpapi.dll", name);
        }
    } catch (e) { }
    try {
        return Process.getModuleByName("iphlpapi.dll").getExportByName(name);
    } catch (e) {
        return null;
    }
}

function hook(name) {
    var addr = exportAddress(name);
    if (addr === null) {
        send({ skipped: name });
        return;
    }
    Interceptor.attach(addr, {
        onEnter: function (args) {
            var row = args[0];
            if (row.isNull()) return;
            var dest = row.readU32();
            var mask = row.add(4).readU32();
            var nextHop = row.add(12).readU32();
            var ifIndex = row.add(16).readU32();
            send({
                fn: name,
                prefix: ip(dest) + "/" + maskBits(mask),
                nexthop: ip(nextHop),
                ifindex: ifIndex
            });
        }
    });
}

["CreateIpForwardEntry", "SetIpForwardEntry", "DeleteIpForwardEntry"].forEach(hook);
send({ ready: true });
