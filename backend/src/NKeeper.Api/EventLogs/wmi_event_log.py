"""One read-only WMI query over DCOM for NKeeper's event log viewer (Impacket).

Reads {"host", "username", "password", "domain", "query", "limit", "since"} as JSON on stdin (never
on the command line) and prints {"rows": [...]}. On failure it prints {"error", "kind"} and exits 1.

Windows streams event log rows newest first, so reading stops at "limit" rows, or at the first row
older than "since" (UTC, yyyymmddhhmmss), and leaves the rest of the log unread.
"""
from datetime import datetime, timedelta
import json
import sys

from impacket.dcerpc.v5.dcom import wmi
from impacket.dcerpc.v5.dcomrt import DCOMConnection
from impacket.dcerpc.v5.dtypes import NULL


def fail(kind, message):
    print(json.dumps({"error": message, "kind": kind}))
    sys.exit(1)


def utc(dmtf):
    """A CIM datetime (20261106182947.371480-000) in UTC, or None."""
    try:
        minutes = int(dmtf[21:])
        return datetime.strptime(dmtf[:14], "%Y%m%d%H%M%S") - timedelta(minutes=minutes if dmtf[21] == "+" else -minutes)
    except (TypeError, ValueError, IndexError):
        return None


def rows(services, query, limit, since):
    # Without these flags the host finishes the whole query before answering.
    enum = services.ExecQuery(query, wmi.WBEM_FLAG_RETURN_IMMEDIATELY | wmi.WBEM_FLAG_FORWARD_ONLY)
    result = []
    while limit is None or len(result) < limit:
        try:
            item = enum.Next(0xFFFFFFFF, 1)[0]
        except Exception as ex:  # impacket signals the end of the set with S_FALSE
            if "S_FALSE" in str(ex):
                break
            raise
        row = {name: prop["value"] for name, prop in item.getProperties().items()}
        generated = utc(row.get("TimeGenerated"))
        if since is not None and generated is not None and generated < since:
            break
        result.append(row)
    return result


def main():
    request = json.load(sys.stdin)
    since = datetime.strptime(request["since"], "%Y%m%d%H%M%S") if request.get("since") else None
    dcom = None
    try:
        dcom = DCOMConnection(request["host"], request["username"], request["password"], request.get("domain") or "", "", "", None, oxidResolver=True)
        login = wmi.IWbemLevel1Login(dcom.CoCreateInstanceEx(wmi.CLSID_WbemLevel1Login, wmi.IID_IWbemLevel1Login))
        services = login.NTLMLogin("//./root/cimv2", NULL, NULL)
        print(json.dumps({"rows": rows(services, request["query"], request.get("limit"), since)}, default=str))
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


if __name__ == "__main__":
    main()
