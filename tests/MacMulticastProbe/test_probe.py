import socket
import struct
import unittest
from unittest.mock import patch

import probe


class ProbeTests(unittest.TestCase):
    def test_valid_packet_must_match_all_evidence(self):
        opts = probe.Options("192.168.8.2", "192.168.8.3", 7, 49001, 49002, "a" * 32, "specific")
        info = (socket.IPPROTO_IP, probe.IP_PKTINFO, struct.pack("=I4s4s", 7, b"\0" * 4, socket.inet_aton(probe.GROUP)))
        body = probe.query(opts.nonce)
        self.assertTrue(probe.accept_query(body, [info], 0, (opts.peer, opts.client_port), opts))
        self.assertFalse(probe.accept_query(body, [info], socket.MSG_TRUNC, (opts.peer, opts.client_port), opts))
        self.assertFalse(probe.accept_query(body, [info], socket.MSG_CTRUNC, (opts.peer, opts.client_port), opts))
        self.assertFalse(probe.accept_query(body, [], 0, (opts.peer, opts.client_port), opts))
        self.assertFalse(probe.accept_query(body, [info, info], 0, (opts.peer, opts.client_port), opts))
        self.assertFalse(probe.accept_query(body + b"x", [info], 0, (opts.peer, opts.client_port), opts))
        self.assertFalse(probe.accept_query(body, [info], 0, ("192.168.8.4", opts.client_port), opts))
        self.assertFalse(probe.accept_query(body, [info], 0, (opts.peer, opts.client_port + 1), opts))
        wrong_interface = (info[0], info[1], struct.pack("=I4s4s", 8, b"\0" * 4, socket.inet_aton(probe.GROUP)))
        wrong_dest = (info[0], info[1], struct.pack("=I4s4s", 7, b"\0" * 4, socket.inet_aton("239.255.82.198")))
        self.assertFalse(probe.accept_query(body, [wrong_interface], 0, (opts.peer, opts.client_port), opts))
        self.assertFalse(probe.accept_query(body, [wrong_dest], 0, (opts.peer, opts.client_port), opts))
        self.assertFalse(probe.accept_query(body, [(info[0], info[1], info[2][:-1])], 0, (opts.peer, opts.client_port), opts))

    def test_input_rejects_bad_values(self):
        valid = dict(local="192.168.8.2", peer="192.168.8.3", index=7, port=49001, client_port=49002, nonce="a" * 32, mode="specific")
        self.assertTrue(probe.valid_options(probe.Options(**valid)))
        for k, v in (("local", "8.8.8.8"), ("peer", "127.0.0.1"), ("peer", valid["local"]), ("index", 0), ("port", 53), ("client_port", 0), ("nonce", "garbage"), ("mode", "other")):
            bad = dict(valid, **{k: v})
            self.assertFalse(probe.valid_options(probe.Options(**bad)), k)

    def test_reply_is_exact_and_bound_to_expected_mac(self):
        o = probe.Options("192.168.8.2", "192.168.8.3", 7, 49001, 49002, "a" * 32, "specific")
        self.assertTrue(probe.accept_reply(probe.reply(o.nonce), (o.local, o.port), o))
        self.assertFalse(probe.accept_reply(probe.reply(o.nonce) + b"x", (o.local, o.port), o))
        self.assertFalse(probe.accept_reply(probe.reply(o.nonce), (o.peer, o.port), o))

    def test_responder_ignores_unmatched_datagram_then_replies_once(self):
        o = probe.Options("192.168.8.2", "192.168.8.3", 7, 49001, 49002, "a" * 32, "specific")
        info = (socket.IPPROTO_IP, probe.IP_PKTINFO, struct.pack("=I4s4s", 7, b"\0" * 4, socket.inet_aton(probe.GROUP)))

        class FakeSocket:
            def __init__(self):
                self.options, self.sent = [], []
                self.packets = [
                    (probe.query(o.nonce), [info], 0, ("192.168.8.4", o.client_port)),
                    (probe.query(o.nonce), [info], 0, (o.peer, o.client_port)),
                ]
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def setsockopt(self, *args): self.options.append(args)
            def getsockopt(self, *args): return o.index
            def bind(self, address): self.address = address
            def settimeout(self, timeout): pass
            def recvmsg(self, *args): return self.packets.pop(0)
            def sendto(self, data, address):
                self.sent.append((data, address))
                return len(data)

        fake = FakeSocket()
        with patch.object(probe.sys, "platform", "darwin"), patch.object(probe.socket, "socket", return_value=fake), patch.object(probe.socket, "CMSG_SPACE", return_value=32, create=True), patch("builtins.print"):
            probe.respond(o)
        self.assertEqual(fake.address, (o.local, o.port))
        self.assertIn((socket.IPPROTO_IP, probe.IP_BOUND_IF, o.index), fake.options)
        self.assertIn((socket.IPPROTO_IP, probe.IP_PKTINFO, 1), fake.options)
        self.assertIn((socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, socket.inet_aton(probe.GROUP) + socket.inet_aton(o.local)), fake.options)
        self.assertEqual(fake.sent, [(probe.reply(o.nonce), (o.peer, o.client_port))])

if __name__ == "__main__":
    unittest.main()
