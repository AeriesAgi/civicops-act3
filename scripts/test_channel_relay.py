#!/usr/bin/env python3
"""Synthetic stdlib relay checks; all sockets are loopback, never provider APIs."""
import base64
import hashlib
import hmac
import http.client
import json
import socket
import sys
import tempfile
import threading
import time
import unittest
from contextlib import closing
from dataclasses import replace
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlencode

sys.dont_write_bytecode = True
import channel_relay as relay

INGRESS = "synthetic-relay-ingress-key"
NATIVE = "synthetic-relay-provider-key"
NUMBER = "+270000000002"
ENV = {
    "CIVICOPS_CHANNEL_WEBHOOK_BASE_URL": "https://municipal.example.invalid/m/synthetic",
    "CIVICOPS_CHANNEL_INGRESS_SECRET": INGRESS,
    "CIVICOPS_TWILIO_AUTH_TOKEN": NATIVE,
    "CIVICOPS_TWILIO_ACCOUNT_SID": "AC00000000000000000000000000000000",
    "CIVICOPS_TWILIO_FROM_NUMBER": NUMBER,
    "CIVICOPS_CLICKATELL_CALLBACK_SECRET": NATIVE,
    "CIVICOPS_CLICKATELL_FROM_NUMBER": NUMBER,
    "CIVICOPS_WHATSAPP_APP_SECRET": NATIVE,
    "CIVICOPS_WHATSAPP_PHONE_NUMBER_ID": "100001",
    "CIVICOPS_WHATSAPP_VERIFY_TOKEN": "synthetic-relay-verify-token",
    "ELEVENLABS_WEBHOOK_SECRET": NATIVE,
    "CIVICOPS_SIP_CALLBACK_SECRET": NATIVE,
}


def fixture(config, provider, text="REPORT Synthetic pipe leak", now=None):
    at = int(time.time()) if now is None else now
    if provider in ("twilio-sms", "twilio-voice"):
        fields = {"AccountSid": ENV["CIVICOPS_TWILIO_ACCOUNT_SID"], "From": "+270000000001", "To": NUMBER}
        fields["MessageSid" if provider == "twilio-sms" else "CallSid"] = "synthetic-provider-0001"
        fields["Body" if provider == "twilio-sms" else "SpeechResult"] = text
        body = urlencode(fields)
        canonical = config.public_url(provider) + "".join(key + fields[key] for key in sorted(fields))
        signature = base64.b64encode(hmac.new(NATIVE.encode(), canonical.encode(), hashlib.sha1).digest()).decode()
        content_type = "application/x-www-form-urlencoded; charset=utf-8"
    else:
        if provider == "clickatell":
            value = {"replyMessageId": "synthetic-reply-0001", "fromNumber": "+270000000001", "toNumber": NUMBER,
                     "timestamp": at * 1000, "text": text}
        elif provider == "whatsapp":
            value = {"object": "whatsapp_business_account", "entry": [{"changes": [{"field": "messages", "value": {
                "metadata": {"phone_number_id": "100001"}, "messages": [{"id": "wamid.synthetic-relay-0001",
                    "from": "270000000001", "timestamp": str(at), "type": "text", "text": {"body": text}}]}}]}]}
        elif provider == "elevenlabs":
            value = {"type": "post_call_transcription", "data": {"conversation_id": "synthetic-call-0001",
                     "analysis": {"transcript_summary": text}}}
        else:
            value = {"event": "speech_summary", "call_id": "synthetic-sip-0001", "from": "+270000000001",
                     "to": NUMBER, "text": text}
        body = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
        content_type = "application/json"
        if provider == "whatsapp":
            signature = "sha256=" + hmac.new(NATIVE.encode(), body.encode(), hashlib.sha256).hexdigest()
        elif provider == "elevenlabs":
            signature = f"t={at},v0=" + hmac.new(NATIVE.encode(), f"{at}.{body}".encode(), hashlib.sha256).hexdigest()
        else:
            signature = NATIVE
    return body, content_type, [(relay.NATIVE_HEADERS[provider], signature)]


class LocalCivicOpsStub(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        raw = self.rfile.read(int(self.headers["Content-Length"]))
        self.server.captures.append((self.path, raw, dict(self.headers.items())))
        body = b'{"received":true,"synthetic":true}'
        self.send_response(self.server.response_status)
        self.send_header("Content-Type", "application/json")
        if self.server.response_status == 302:
            self.send_header("Location", "https://provider.example.invalid/never-follow")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self.server.captures.append((self.path, b"", dict(self.headers.items())))
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", "6")
        self.end_headers()
        self.wfile.write(b"101010")


class RelayConfigTests(unittest.TestCase):
    def test_defaults_are_literal_loopback(self):
        config = relay.RelayConfig.from_environ(ENV)
        self.assertEqual(("127.0.0.1", 5080, 5092), (config.target_host, config.target_port, config.port))
        self.assertEqual("/m/synthetic/api/v1/channels/webhooks/", config.path_prefix)
        self.assertNotIn(INGRESS, repr(config))
        self.assertNotIn(NATIVE, repr(config))

    def test_external_dns_and_non_loopback_targets_fail_closed(self):
        for target in ("http://example.invalid", "http://localhost:5080", "http://10.0.0.1:5080", "http://0.0.0.0:5080",
                       "https://127.0.0.1:5080", "http://127.0.0.1:5080@evil.invalid", "http://[::ffff:10.0.0.1]:5080"):
            with self.subTest(target=target), self.assertRaises(relay.RelayError):
                relay.RelayConfig.from_environ(ENV | {"CIVICOPS_CHANNEL_RELAY_TARGET": target})

    def test_target_paths_credentials_queries_and_invalid_ports_fail_closed(self):
        for target in ("http://127.0.0.1:5080/m/other", "http://user:pass@127.0.0.1:5080", "http://127.0.0.1:5080?x=y",
                       "http://127.0.0.1:5080#fragment", "http://127.0.0.1:65536", "http://127.0.0.1:0",
                       "http://127.0.0.1:bad", "http://[invalid-ipv6"):
            with self.subTest(target=target), self.assertRaises(relay.RelayError):
                relay.RelayConfig.from_environ(ENV | {"CIVICOPS_CHANNEL_RELAY_TARGET": target})

    def test_ipv6_literal_loopback_target_is_supported(self):
        config = relay.RelayConfig.from_environ(ENV | {"CIVICOPS_CHANNEL_RELAY_TARGET": "http://[::1]:5081"})
        self.assertEqual(("::1", 5081), (config.target_host, config.target_port))

    def test_untrusted_public_base_and_missing_ingress_fail_closed(self):
        for base in ("http://municipal.example.invalid", "https://user:pass@municipal.example.invalid",
                     "https://municipal.example.invalid/other", "https://municipal.example.invalid/m/a?x=y",
                     "https://municipal.example.invalid/m/a#fragment", "https://municipal.example.invalid:bad",
                     "https://municipal.example.invalid:0", "https://municipal example.invalid", ""):
            with self.subTest(base=base), self.assertRaises(relay.RelayError):
                relay.RelayConfig.from_environ(ENV | {"CIVICOPS_CHANNEL_WEBHOOK_BASE_URL": base})
        with self.assertRaises(relay.RelayError):
            relay.RelayConfig.from_environ(ENV | {"CIVICOPS_CHANNEL_INGRESS_SECRET": ""})

    def test_mounted_secret_precedes_environment_and_trims_final_newline(self):
        with tempfile.TemporaryDirectory(prefix="civicops-relay-secrets-") as directory:
            source = Path(directory) / "synthetic-secret"
            source.write_text("synthetic-mounted-key\r\n")
            env = ENV | {"CIVICOPS_CHANNEL_INGRESS_SECRET_FILE": str(source)}
            self.assertEqual("synthetic-mounted-key", relay.RelayConfig.from_environ(env).ingress_secret)

    def test_invalid_secret_sources_do_not_leak_paths_or_values(self):
        with tempfile.TemporaryDirectory(prefix="civicops-relay-secrets-") as directory:
            source = Path(directory) / "synthetic-private-source"
            for content in (b"", b"x" * 16385, b"synthetic\nsecret", b"\xff"):
                source.write_bytes(content)
                with self.subTest(length=len(content)), self.assertRaises(relay.RelayError) as caught:
                    relay.secret_value({"KEY_FILE": str(source)}, "KEY")
                self.assertNotIn(directory, str(caught.exception))
            with self.assertRaises(relay.RelayError):
                relay.secret_value({"KEY_FILE": "relative-secret"}, "KEY")
            with self.assertRaises(relay.RelayError):
                relay.secret_value({"KEY": "synthetic\x00secret"}, "KEY")


class RelayNativeTests(unittest.TestCase):
    def setUp(self):
        self.config = relay.RelayConfig.from_environ(ENV)

    def test_twilio_official_signature_vector(self):
        config = replace(self.config, public_base="https://example.com/myapp.php?foo=1&bar=2")
        fields = {"CallSid": "CA1234567890ABCDE", "Caller": "+14158675310", "Digits": "1234", "From": "+14158675310", "To": "+18005551212"}
        canonical = config.public_base + "".join(key + fields[key] for key in sorted(fields))
        actual = base64.b64encode(hmac.new(b"12345", canonical.encode(), hashlib.sha1).digest()).decode()
        self.assertEqual("L/OH5YylLD5NRKLltdqwSvS0BnU=", actual)

    def test_twilio_signature_binds_trusted_tenant_and_full_form(self):
        body, content, headers = fixture(self.config, "twilio-sms")
        for altered in (replace(self.config, public_base="https://municipal.example.invalid/m/other"),):
            with self.assertRaises(relay.RelayError) as caught:
                relay.verify_native(altered, "twilio-sms", body, content, headers)
            self.assertEqual(401, caught.exception.status)
        with self.assertRaises(relay.RelayError):
            relay.verify_native(self.config, "twilio-sms", body + "&Body=changed", content, headers)

    def test_receiver_binding_cannot_be_replaced_by_valid_native_signature(self):
        for provider, binding in (("twilio-sms", "twilio-number"), ("clickatell", "clickatell-number"), ("whatsapp", "whatsapp-number")):
            body, content, headers = fixture(self.config, provider)
            altered = replace(self.config, bindings=self.config.bindings | {binding: "100999" if provider == "whatsapp" else "+270000000999"})
            with self.subTest(provider=provider), self.assertRaises(relay.RelayError) as caught:
                relay.verify_native(altered, provider, body, content, headers)
            self.assertEqual(401, caught.exception.status)

    def test_elevenlabs_expired_future_and_duplicate_timestamps_fail(self):
        at = int(time.time())
        for timestamp in (at - 1801, at + 31):
            body, content, headers = fixture(self.config, "elevenlabs", now=timestamp)
            with self.subTest(timestamp=timestamp), self.assertRaises(relay.RelayError):
                relay.verify_native(self.config, "elevenlabs", body, content, headers, at)
        body, content, headers = fixture(self.config, "elevenlabs", now=at)
        with self.assertRaises(relay.RelayError):
            relay.verify_native(self.config, "elevenlabs", body, content, [(headers[0][0], headers[0][1] + f",t={at}")], at)

    def test_duplicate_authentication_headers_are_rejected(self):
        for provider in relay.PROVIDERS:
            body, content, headers = fixture(self.config, provider)
            with self.subTest(provider=provider), self.assertRaises(relay.RelayError):
                relay.verify_native(self.config, provider, body, content, headers + [(headers[0][0].lower(), headers[0][1])])

    def test_nonascii_native_digest_is_a_bounded_authentication_failure(self):
        for provider in ("twilio-sms", "twilio-voice", "whatsapp", "elevenlabs"):
            body, content, headers = fixture(self.config, provider)
            with self.subTest(provider=provider), self.assertRaises(relay.RelayError) as invalid:
                relay.verify_native(self.config, provider, body, content, [(headers[0][0], "synthetic-\u00ff-digest")])
            self.assertEqual(401, invalid.exception.status)

    def test_valid_native_signature_cannot_replace_expected_content_type(self):
        for provider in relay.PROVIDERS:
            body, _, headers = fixture(self.config, provider)
            with self.subTest(provider=provider), self.assertRaises(relay.RelayError) as invalid:
                relay.verify_native(self.config, provider, body, "application/octet-stream", headers)
            self.assertEqual(400, invalid.exception.status)

    def test_form_duplicate_invalid_percent_and_invalid_utf8_are_rejected(self):
        for body in ("From=x&From=y", "Body=%ZZ", "Body=%FF", "&".join(f"x{i}=y" for i in range(65))):
            with self.subTest(body=body), self.assertRaises(relay.RelayError):
                relay.bounded_form(body)

    def test_json_duplicates_nonfinite_surrogates_depth_and_array_bounds_are_rejected(self):
        for body in ('{"x":1,"x":2}', '{"x":NaN}', '{"x":1e999}', '{"\\ud800":1}', '{"x":"\\ud800"}',
                     '{"x":' + "[" * 17 + "0" + "]" * 17 + "}",
                     '{"x":[' + ",".join("0" for _ in range(501)) + "]}", "[]"):
            with self.subTest(body=body[:40]), self.assertRaises(relay.RelayError):
                relay.bounded_json(body)

    def test_new_envelopes_bind_provider_body_tenant_and_create_unique_retry_nonces(self):
        body, content, native = fixture(self.config, "whatsapp", text="REPORT Synthetic water: amanzi")
        relay.verify_native(self.config, "whatsapp", body, content, native)
        first = relay.ingress_headers(self.config, "whatsapp", body, content, native[0][1])
        retry = relay.ingress_headers(self.config, "whatsapp", body, content, native[0][1])
        self.assertNotEqual(first["X-CivicOps-Nonce"], retry["X-CivicOps-Nonce"])
        self.assertEqual(32, len(first["X-CivicOps-Nonce"]))
        canonical = first["X-CivicOps-Timestamp"] + "." + first["X-CivicOps-Nonce"] + ".whatsapp." + self.config.public_url("whatsapp") + "." + body
        expected = "sha256=" + hmac.new(INGRESS.encode(), canonical.encode(), hashlib.sha256).hexdigest()
        self.assertEqual(expected, first["X-CivicOps-Signature"])
        altered = canonical.replace(".whatsapp.", ".sip.", 1)
        self.assertNotEqual(expected, "sha256=" + hmac.new(INGRESS.encode(), altered.encode(), hashlib.sha256).hexdigest())


def native_test(provider):
    def check(self):
        body, content, headers = fixture(self.config, provider)
        self.assertEqual(headers[0][1], relay.verify_native(self.config, provider, body, content, headers))
        with self.assertRaises(relay.RelayError) as invalid:
            relay.verify_native(self.config, provider, body, content, [(headers[0][0], "synthetic-invalid-signature")])
        self.assertEqual(401, invalid.exception.status)
        with self.assertRaises(relay.RelayError) as disabled:
            relay.verify_native(replace(self.config, secrets=self.config.secrets | {provider: ""}), provider, body, content, headers)
        self.assertEqual(503, disabled.exception.status)
    return check


for _provider in sorted(relay.PROVIDERS):
    setattr(RelayNativeTests, "test_native_authentication_" + _provider.replace("-", "_"), native_test(_provider))


class RelayHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.local = ThreadingHTTPServer(("127.0.0.1", 0), LocalCivicOpsStub)
        cls.local.captures = []
        cls.local.response_status = 200
        cls.config = relay.RelayConfig.from_environ(ENV | {"CIVICOPS_CHANNEL_RELAY_TARGET": f"http://127.0.0.1:{cls.local.server_port}"})
        cls.gateway = relay.RelayServer(cls.config, port=0)
        cls.threads = [threading.Thread(target=server.serve_forever, daemon=True) for server in (cls.local, cls.gateway)]
        for thread in cls.threads:
            thread.start()

    @classmethod
    def tearDownClass(cls):
        for server in (cls.gateway, cls.local):
            server.shutdown()
            server.server_close()
        for thread in cls.threads:
            thread.join(timeout=5)

    def setUp(self):
        self.local.captures.clear()
        self.local.response_status = 200

    def request(self, method, path, body=None, headers=None):
        with closing(http.client.HTTPConnection("127.0.0.1", self.gateway.server_port, timeout=5)) as connection:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            return response.status, response.read(), dict(response.headers.items())

    def post(self, provider, body=None, headers=None, content=None, path=None):
        fixture_body, fixture_content, native = fixture(self.config, provider)
        return self.request("POST", path or self.config.path_prefix + provider, fixture_body.encode() if body is None else body,
                            {"Content-Type": content or fixture_content} | dict(native) | (headers or {}))

    def test_spoofed_ingress_host_forwarding_cookie_and_credentials_are_stripped(self):
        status, _, _ = self.post("whatsapp", headers={"Host": "evil.example.invalid", "X-Forwarded-Host": "evil.example.invalid",
                "X-CivicOps-Timestamp": "1", "X-CivicOps-Nonce": "attacker-nonce-0001", "X-CivicOps-Signature": "sha256=spoofed",
                "Authorization": "Bearer synthetic-attacker", "Cookie": "synthetic-attacker-cookie"})
        self.assertEqual(200, status)
        path, body, headers = self.local.captures[0]
        self.assertEqual(self.config.path_prefix + "whatsapp", path)
        self.assertEqual(f"127.0.0.1:{self.local.server_port}", headers["Host"])
        self.assertNotEqual("attacker-nonce-0001", headers["X-CivicOps-Nonce"])
        self.assertGreater(int(headers["X-CivicOps-Timestamp"]), 1)
        for key in ("Authorization", "Cookie", "X-Forwarded-Host"):
            self.assertNotIn(key, headers)
        canonical = headers["X-CivicOps-Timestamp"] + "." + headers["X-CivicOps-Nonce"] + ".whatsapp." + self.config.public_url("whatsapp") + "." + body.decode()
        self.assertEqual("sha256=" + hmac.new(INGRESS.encode(), canonical.encode(), hashlib.sha256).hexdigest(), headers["X-CivicOps-Signature"])

    def test_invalid_native_is_rejected_before_local_forward(self):
        status, _, _ = self.post("whatsapp", headers={"X-Hub-Signature-256": "sha256=invalid", "X-CivicOps-Signature": "sha256=spoofed"})
        self.assertEqual(401, status)
        self.assertEqual([], self.local.captures)

    def test_excessive_header_count_is_rejected_before_local_forward(self):
        headers = {f"X-Synthetic-{i}": "bounded" for i in range(65)}
        self.assertEqual(431, self.post("whatsapp", headers=headers)[0])
        self.assertEqual([], self.local.captures)

    def test_unsupported_method_does_not_echo_resident_path(self):
        marker = "synthetic-private-marker"
        status, body, _ = self.request("DELETE", "/" + marker)
        self.assertEqual(501, status)
        self.assertNotIn(marker.encode(), body)
        self.assertEqual([], self.local.captures)

    def test_absolute_deadline_stops_a_slow_header_without_forwarding(self):
        with relay.RelayServer(self.config, port=0, request_deadline_seconds=0.2) as gateway:
            thread = threading.Thread(target=gateway.serve_forever, daemon=True)
            thread.start()
            try:
                with socket.create_connection(("127.0.0.1", gateway.server_port), timeout=2) as connection:
                    connection.sendall(b"POST /m/synthetic/api/v1/channels/webhooks/whatsapp HTTP/1.1\r\nHost: synthetic")
                    for _ in range(6):
                        time.sleep(0.05)
                        try:
                            connection.sendall(b"x")
                        except OSError:
                            break
                    self.assertEqual(b"", connection.recv(128))
                self.assertEqual([], self.local.captures)
            finally:
                gateway.shutdown()
                thread.join(timeout=5)

    def test_cross_tenant_unknown_query_and_absolute_routes_are_rejected(self):
        for path in ("/m/other/api/v1/channels/webhooks/whatsapp", self.config.path_prefix + "unknown",
                     self.config.path_prefix + "whatsapp?query=spoofed", "http://evil.example.invalid" + self.config.path_prefix + "whatsapp"):
            with self.subTest(path=path):
                self.assertEqual(404, self.post("whatsapp", path=path)[0])
        self.assertEqual([], self.local.captures)

    def test_transfer_encoding_oversized_body_and_invalid_utf8_are_rejected(self):
        for body, headers, expected in ((b"x", {"Transfer-Encoding": "chunked"}, 400), (b"x" * (relay.MAX_BODY + 1), {}, 413),
                                       (b"\xff", {}, 400)):
            with self.subTest(expected=expected):
                self.assertEqual(expected, self.post("whatsapp", body=body, headers=headers)[0])
        self.assertEqual([], self.local.captures)

    def test_verified_provider_retry_is_forwarded_with_a_new_nonce(self):
        self.assertEqual(200, self.post("twilio-sms")[0])
        self.assertEqual(200, self.post("twilio-sms")[0])
        self.assertEqual(self.local.captures[0][1], self.local.captures[1][1])
        self.assertNotEqual(self.local.captures[0][2]["X-CivicOps-Nonce"], self.local.captures[1][2]["X-CivicOps-Nonce"])

    def test_local_redirect_is_never_followed(self):
        self.local.response_status = 302
        status, body, headers = self.post("clickatell")
        self.assertEqual(502, status)
        self.assertEqual(1, len(self.local.captures))
        self.assertNotIn("Location", headers)
        self.assertNotIn(b"provider.example.invalid", body)

    def test_meta_challenge_is_verified_and_forwarded_only_to_loopback(self):
        query = urlencode({"hub.mode": "subscribe", "hub.challenge": "101010", "hub.verify_token": ENV["CIVICOPS_WHATSAPP_VERIFY_TOKEN"]})
        status, body, _ = self.request("GET", self.config.path_prefix + "whatsapp?" + query)
        self.assertEqual((200, b"101010"), (status, body))
        self.assertEqual(1, len(self.local.captures))
        self.assertNotIn("X-CivicOps-Signature", self.local.captures[0][2])

    def test_invalid_duplicate_cross_provider_challenges_are_never_forwarded(self):
        query = "hub.mode=subscribe&hub.challenge=101010&hub.verify_token=synthetic-invalid"
        self.assertEqual(401, self.request("GET", self.config.path_prefix + "whatsapp?" + query)[0])
        self.assertEqual(400, self.request("GET", self.config.path_prefix + "whatsapp?" + query + "&hub.verify_token=another")[0])
        self.assertEqual(404, self.request("GET", self.config.path_prefix + "sip?" + query)[0])
        self.assertEqual([], self.local.captures)

    def test_error_responses_do_not_include_raw_path_body_or_query(self):
        marker = "synthetic-private-marker"
        status, body, _ = self.request("GET", self.config.path_prefix + "whatsapp?" + marker)
        self.assertEqual(400, status)
        self.assertNotIn(marker.encode(), body)
        self.assertEqual("no-store", self.request("GET", "/" + marker)[2]["Cache-Control"])


def forwarding_test(provider):
    def check(self):
        body, content, native = fixture(self.config, provider, text="REPORT Synthetic água/amanzi/vuur leak 🔧")
        status, _, headers = self.post(provider, body=body.encode(), content=content, headers=dict(native))
        self.assertEqual(200, status)
        self.assertEqual("no-store", headers["Cache-Control"])
        path, captured, signed = self.local.captures[0]
        self.assertEqual(self.config.path_prefix + provider, path)
        self.assertEqual(body.encode(), captured)
        self.assertEqual(native[0][1], signed[native[0][0]])
        self.assertEqual(32, len(signed["X-CivicOps-Nonce"]))
    return check


for _provider in sorted(relay.PROVIDERS):
    setattr(RelayHttpTests, "test_forward_preserves_native_" + _provider.replace("-", "_"), forwarding_test(_provider))


if __name__ == "__main__":
    unittest.main(verbosity=2)
