"""Read-only WMI queries over DCOM for NKeeper's Windows internal scan (Impacket).

Reads {"host", "username", "password", "domain"} as JSON on stdin (never on the command
line) and prints one JSON document. On failure it prints {"error", "kind"} and exits 1.
"""
import json
import sys

from impacket.dcerpc.v5.dcom import wmi
from impacket.dcerpc.v5.dcomrt import DCOMConnection
from impacket.dcerpc.v5.dtypes import NULL

QUERIES = {
    "system": ("root/cimv2", "SELECT DNSHostName, Name, Manufacturer, Model, NumberOfLogicalProcessors, TotalPhysicalMemory FROM Win32_ComputerSystem"),
    "bios": ("root/cimv2", "SELECT SerialNumber FROM Win32_BIOS"),
    "memory": ("root/cimv2", "SELECT Capacity FROM Win32_PhysicalMemory"),
    "os": ("root/cimv2", "SELECT Caption, Version FROM Win32_OperatingSystem"),
    "adapters": ("root/cimv2", "SELECT IPAddress, MACAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"),
    "users": ("root/cimv2", "SELECT Name, FullName, SID FROM Win32_UserAccount WHERE LocalAccount = TRUE AND Disabled = FALSE AND Lockout = FALSE"),
    # State 2 is "Listen"; needs Windows 8 / Server 2012 or later.
    "ports": ("root/StandardCimv2", "SELECT LocalAddress, LocalPort FROM MSFT_NetTCPConnection WHERE State = 2"),
}


def fail(kind, message):
    print(json.dumps({"error": message, "kind": kind}))
    sys.exit(1)


def rows(services, query):
    enum = services.ExecQuery(query)
    result = []
    while True:
        try:
            item = enum.Next(0xFFFFFFFF, 1)[0]
        except Exception as ex:  # impacket signals the end of the set with S_FALSE
            if "S_FALSE" in str(ex):
                break
            raise
        result.append({name: prop["value"] for name, prop in item.getProperties().items()})
    return result


def main():
    request = json.load(sys.stdin)
    dcom = None
    try:
        dcom = DCOMConnection(request["host"], request["username"], request["password"], request.get("domain") or "", "", "", None, oxidResolver=True)
        login = wmi.IWbemLevel1Login(dcom.CoCreateInstanceEx(wmi.CLSID_WbemLevel1Login, wmi.IID_IWbemLevel1Login))

        output, notes = {}, []
        services_by_namespace = {}
        for key, (namespace, query) in QUERIES.items():
            try:
                if namespace not in services_by_namespace:
                    services_by_namespace[namespace] = login.NTLMLogin("//./" + namespace, NULL, NULL)
                output[key] = rows(services_by_namespace[namespace], query)
            except Exception as ex:
                if key == "ports":
                    notes.append("The listening ports couldn't be read (this needs Windows 8 or Server 2012 or later).")
                    output[key] = []
                elif key in ("bios", "memory"):
                    output[key] = []
                else:
                    raise
        output["notes"] = notes
        print(json.dumps(output, default=str))
    except Exception as ex:
        text = str(ex)
        lowered = text.lower()
        if "logon_failure" in lowered or "access_denied" in lowered or "accessdenied" in lowered:
            fail("auth", "Windows refused the login. Check the username, password and domain, and that the account may use remote WMI (DCOM).")
        if any(s in lowered for s in ("connection refused", "timed out", "unreachable", "no route", "name or service not known", "connection reset")):
            fail("connect", "Couldn't reach {} over RPC. Check that it is up and its firewall allows WMI (DCOM, port 135).".format(request["host"]))
        fail("error", "The WMI query failed: " + text)
    finally:
        if dcom is not None:
            try:
                dcom.disconnect()
            except Exception:
                pass


main()
