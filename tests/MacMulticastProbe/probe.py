"""Opt-in synthetic multicast diagnostic; never loads product data or credentials."""
import argparse
import ipaddress
import json
import re
import socket
import struct
import sys
import time
from dataclasses import dataclass

GROUP = "239.255.82.197"
IP_BOUND_IF = 25  # Darwin <netinet/in.h>
IP_PKTINFO = 26    # Darwin <netinet/in.h>
_RFC1918 = tuple(ipaddress.ip_network(s) for s in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))


@dataclass(frozen=True)
class Options:
    local: str       # Mac address
    peer: str        # Windows address
    index: int       # Mac interface index
    port: int        # Mac UDP port
    client_port: int # Windows UDP port
    nonce: str       # 16 bytes of non-secret random test data, in lowercase hex
    mode: str        # specific or any; 'any' is diagnostic only


def valid_options(o):
    try:
        addresses = [ipaddress.IPv4Address(x) for x in (o.local, o.peer)]
        return (addresses[0] != addresses[1] and
                all(any(a in subnet for subnet in _RFC1918) for a in addresses) and
                0 < o.index <= 0x7fffffff and 1024 <= o.port <= 65535 and
                1024 <= o.client_port <= 65535 and o.port != o.client_port and
                re.fullmatch(r"[0-9a-f]{32}", o.nonce) is not None and
                o.mode in ("specific", "any"))
    except (ValueError, TypeError):
        return False


def query(nonce):
    return b"NIAN-SOCKET-PROBE-QUERY-v1:" + bytes.fromhex(nonce)


def reply(nonce):
    return b"NIAN-SOCKET-PROBE-REPLY-v1:" + bytes.fromhex(nonce)


def accept_query(data, ancillary, flags, source, o):
    if flags & (socket.MSG_TRUNC | socket.MSG_CTRUNC) or source != (o.peer, o.client_port) or data != query(o.nonce):
        return False
    info = [c[2] for c in ancillary if c[0] == socket.IPPROTO_IP and c[1] == IP_PKTINFO]
    if len(info) != 1 or len(info[0]) != 12:
        return False
    index, _local, destination = struct.unpack("=I4s4s", info[0])
    return index == o.index and destination == socket.inet_aton(GROUP)


def accept_reply(data, source, o):
    return data == reply(o.nonce) and source == (o.local, o.port)


def respond(o):
    if sys.platform != "darwin":
        raise RuntimeError("Mac only")
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.IPPROTO_IP, IP_BOUND_IF, o.index)
        if sock.getsockopt(socket.IPPROTO_IP, IP_BOUND_IF) != o.index:
            raise RuntimeError("interface not bound")
        sock.setsockopt(socket.IPPROTO_IP, IP_PKTINFO, 1)
        sock.bind((o.local if o.mode == "specific" else "0.0.0.0", o.port))
        sock.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP,
                        socket.inet_aton(GROUP) + socket.inet_aton(o.local))
        deadline = time.monotonic() + 10
        print(json.dumps({"status": "ready"}), flush=True)
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError()
            sock.settimeout(remaining)
            data, ancillary, flags, source = sock.recvmsg(128, socket.CMSG_SPACE(12))
            if accept_query(data, ancillary, flags, source, o):
                if sock.sendto(reply(o.nonce), (o.peer, o.client_port)) != len(reply(o.nonce)):
                    raise RuntimeError("short send")
                return


def send(o):
    if sys.platform != "win32":
        raise RuntimeError("Windows only")
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind((o.peer, o.client_port))
        sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(o.peer))
        sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)
        sock.settimeout(8)
        if sock.sendto(query(o.nonce), (GROUP, o.port)) != len(query(o.nonce)):
            raise RuntimeError("short send")
        deadline = time.monotonic() + 8
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError()
            sock.settimeout(remaining)
            data, source = sock.recvfrom(128)
            if accept_reply(data, source, o):
                return


def main():
    if len(sys.argv) == 1:
        print(json.dumps({"status": "not_started"}))
        return 2
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("respond", "send"))
    parser.add_argument("--run", action="store_true", required=True)
    parser.add_argument("--local", required=True)
    parser.add_argument("--peer", required=True)
    parser.add_argument("--index", type=int, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--client-port", type=int, required=True)
    parser.add_argument("--nonce", required=True)
    parser.add_argument("--mode", choices=("specific", "any"), required=True)
    args = parser.parse_args()
    o = Options(args.local, args.peer, args.index, args.port, args.client_port, args.nonce, args.mode)
    if not valid_options(o):
        print(json.dumps({"status": "invalid"}))
        return 2
    try:
        (respond if args.action == "respond" else send)(o)
    except (OSError, RuntimeError):
        print(json.dumps({"status": "failed"}), flush=True)
        return 1
    print(json.dumps({"status": "ok"}), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
