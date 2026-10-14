#!/usr/bin/env python3
"""Authenticated native webhook relay; forwards only to a local CivicOps server.

No provider requests, media fetches, raw-body persistence or resident-data logging.
An owner-operated HTTPS ingress must terminate TLS in front of this loopback listener.
Provider retries receive new ingress nonces; CivicOps durably deduplicates provider IDs.
"""
import base64
import hashlib
import hmac
import http.client
import ipaddress
import json
import math
import os
import re
import socket
import threading
import time
import uuid
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qsl, urlsplit

MAX_BODY = 256 * 1024
PROVIDERS = frozenset(("twilio-sms", "twilio-voice", "clickatell", "whatsapp", "elevenlabs", "sip"))
PATH_SUFFIX = "/api/v1/channels/webhooks/"
NATIVE_HEADERS = {"twilio-sms": "X-Twilio-Signature", "twilio-voice": "X-Twilio-Signature",
                  "clickatell": "X-CivicOps-Clickatell-Token", "whatsapp": "X-Hub-Signature-256",
                  "elevenlabs": "ElevenLabs-Signature", "sip": "X-CivicOps-Sip-Token"}


class RelayError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def secret_value(env, name):
    """Mounted files take precedence; errors deliberately omit paths and values."""
    filename = env.get(name + "_FILE", "")
    try:
        if filename.strip():
            path = Path(filename)
            if not path.is_absolute():
                raise ValueError()
            with path.open("rb") as source:
                raw = source.read(16385)
            if not 0 < len(raw) <= 16384:
                raise ValueError()
            value = raw.decode("utf-8", "strict").rstrip("\r\n")
        else:
            value = env.get(name, "")
        if not value.strip():
            return ""
        if len(value.encode("utf-8")) > 16384 or any(ord(c) < 32 or ord(c) == 127 for c in value):
            raise ValueError()
        return value
    except (OSError, ValueError, UnicodeError):
        raise RelayError(503, "Configured secret source is unavailable or invalid.") from None


@dataclass(frozen=True)
class RelayConfig:
    public_base: str
    target_host: str
    target_port: int
    ingress_secret: str = field(repr=False)
    secrets: dict = field(repr=False)
    bindings: dict = field(repr=False)
    port: int = 5092

    @property
    def path_prefix(self):
        return urlsplit(self.public_base).path + PATH_SUFFIX

    def public_url(self, provider):
        return self.public_base + PATH_SUFFIX + provider

    @classmethod
    def from_environ(cls, env=None):
        env = os.environ if env is None else env
        public = env.get("CIVICOPS_CHANNEL_WEBHOOK_BASE_URL", "").rstrip("/")
        try:
            base = urlsplit(public)
            public_port = base.port
        except ValueError:
            raise RelayError(503, "Configure a trusted HTTPS municipal webhook base URL.") from None
        # Match the server's configured municipal base exactly, never Host/forwarded headers.
        if (base.scheme != "https" or not base.hostname or base.username or base.password or base.query or base.fragment
                or len(public) > 2048 or any(c.isspace() or ord(c) < 32 for c in public)
                or public_port is not None and not 1 <= public_port <= 65535
                or not re.fullmatch(r"(?:/m/[a-z0-9][a-z0-9-]{0,47})?", base.path)):
            raise RelayError(503, "Configure a trusted HTTPS municipal webhook base URL.")
        try:
            target = urlsplit(env.get("CIVICOPS_CHANNEL_RELAY_TARGET", "http://127.0.0.1:5080"))
            address = ipaddress.ip_address(target.hostname or "")
            target_port = 80 if target.port is None else target.port
            port = int(env.get("CIVICOPS_CHANNEL_RELAY_PORT", "5092") or "5092")
            if (target.scheme != "http" or not address.is_loopback or target.username or target.password
                    or target.path not in ("", "/") or target.query or target.fragment
                    or not 1 <= target_port <= 65535 or not 1 <= port <= 65535):
                raise ValueError()
        except ValueError:
            raise RelayError(503, "Relay target must be literal loopback HTTP; configure valid ports.") from None
        ingress = secret_value(env, "CIVICOPS_CHANNEL_INGRESS_SECRET")
        if not ingress:
            raise RelayError(503, "Configure the CivicOps ingress signing secret.")
        names = {"twilio-sms": "CIVICOPS_TWILIO_AUTH_TOKEN", "twilio-voice": "CIVICOPS_TWILIO_AUTH_TOKEN",
                 "clickatell": "CIVICOPS_CLICKATELL_CALLBACK_SECRET", "whatsapp": "CIVICOPS_WHATSAPP_APP_SECRET",
                 "elevenlabs": "ELEVENLABS_WEBHOOK_SECRET", "sip": "CIVICOPS_SIP_CALLBACK_SECRET",
                 "whatsapp-verify": "CIVICOPS_WHATSAPP_VERIFY_TOKEN"}
        secrets = {key: secret_value(env, name) for key, name in names.items()}
        bindings = {"account": env.get("CIVICOPS_TWILIO_ACCOUNT_SID", ""),
                    "twilio-number": env.get("CIVICOPS_TWILIO_FROM_NUMBER", ""),
                    "clickatell-number": env.get("CIVICOPS_CLICKATELL_FROM_NUMBER", ""),
                    "whatsapp-number": env.get("CIVICOPS_WHATSAPP_PHONE_NUMBER_ID", "")}
        return cls(public, str(address), target_port, ingress, secrets, bindings, port)


def single_header(headers, name):
    matches = [value for key, value in headers if key.lower() == name.lower()]
    if len(matches) != 1 or len(matches[0]) > 16384:
        return ""
    return matches[0]


def equal_secret(expected, actual):
    return bool(expected and actual) and hmac.compare_digest(hashlib.sha256(expected.encode()).digest(),
                                                            hashlib.sha256(actual.encode()).digest())


def bounded_form(body):
    if re.search(r"%(?![0-9a-fA-F]{2})", body):
        raise RelayError(400, "Invalid provider form encoding.")
    try:
        pairs = parse_qsl(body, keep_blank_values=True, encoding="utf-8", errors="strict", max_num_fields=64)
    except (ValueError, UnicodeError):
        raise RelayError(400, "Invalid provider form encoding.") from None
    result = {}
    for key, value in pairs:
        if key in result or not 0 < len(key) <= 128 or len(value) > 16384:
            raise RelayError(400, "Invalid or duplicate provider form field.")
        result[key] = value
    return result


def bounded_json(body):
    def object_hook(pairs):
        result = {}
        for key, value in pairs:
            key.encode("utf-8", "strict")
            if key in result or not 0 < len(key) <= 128 or len(result) >= 512:
                raise ValueError()
            result[key] = value
        return result

    def inspect(value, depth=0):
        if depth > 16:
            raise ValueError()
        if isinstance(value, dict):
            for child in value.values():
                inspect(child, depth + 1)
        elif isinstance(value, list):
            if len(value) > 500:
                raise ValueError()
            for child in value:
                inspect(child, depth + 1)
        elif isinstance(value, str):
            value.encode("utf-8", "strict")  # Reject lone surrogate escape sequences.
        elif isinstance(value, float) and not math.isfinite(value):
            raise ValueError()

    try:
        value = json.loads(body, object_pairs_hook=object_hook, parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
        if not isinstance(value, dict):
            raise ValueError()
        inspect(value)
        return value
    except (ValueError, UnicodeError, RecursionError):
        raise RelayError(400, "Invalid or oversized provider JSON.") from None


def normalize_phone(value):
    return "+" + value.lstrip("+") if isinstance(value, str) and re.fullmatch(r"\+?[0-9]{7,15}", value) else ""


def verify_native(config, provider, body, content_type, headers, now=None):
    """Verify before generating any trusted CivicOps headers. No native freshness is invented."""
    native = config.secrets.get(provider, "")
    if not native:
        raise RelayError(503, "This provider's authenticated relay is disabled.")
    supplied = single_header(headers, NATIVE_HEADERS[provider])
    if provider in ("twilio-sms", "twilio-voice", "whatsapp", "elevenlabs") and not supplied.isascii():
        raise RelayError(401, "Native provider authentication failed.")
    media_type = content_type.split(";", 1)[0].strip().lower()
    now = int(time.time()) if now is None else now
    if provider in ("twilio-sms", "twilio-voice"):
        if media_type != "application/x-www-form-urlencoded":
            raise RelayError(400, "Send a provider form webhook.")
        fields = bounded_form(body)
        canonical = config.public_url(provider) + "".join(key + fields[key] for key in sorted(fields))
        # Twilio mandates HMAC-SHA1 for these native callbacks; ingress uses HMAC-SHA256.
        expected = base64.b64encode(hmac.new(native.encode(), canonical.encode(), hashlib.sha1).digest()).decode()
        valid = hmac.compare_digest(expected, supplied)
        account = config.bindings.get("account")
        number = normalize_phone(config.bindings.get("twilio-number"))
        if not account or not number:
            raise RelayError(503, "Configure the provider account and receiving number.")
        valid = valid and fields.get("AccountSid") == account and normalize_phone(fields.get("To")) == number
    elif provider == "elevenlabs":
        parts = {}
        for item in supplied.split(","):
            pair = item.strip().split("=", 1)
            if len(pair) != 2 or pair[0] in parts:
                raise RelayError(401, "Native provider authentication failed.")
            parts[pair[0]] = pair[1]
        timestamp = parts.get("t", "")
        if not re.fullmatch(r"[0-9]{1,16}", timestamp) or not now - 1800 <= int(timestamp) <= now + 30:
            raise RelayError(401, "Native provider authentication failed.")
        expected = hmac.new(native.encode(), (timestamp + "." + body).encode(), hashlib.sha256).hexdigest()
        valid = hmac.compare_digest(expected, parts.get("v0", "").lower())
    elif provider == "whatsapp":
        expected = "sha256=" + hmac.new(native.encode(), body.encode(), hashlib.sha256).hexdigest()
        valid = supplied.startswith("sha256=") and hmac.compare_digest(expected, "sha256=" + supplied[7:].lower())
    else:
        # Clickatell documents custom static callback headers, not a native body HMAC.
        # SIP is an owner gateway protocol, never an anonymously trusted provider event.
        valid = equal_secret(native, supplied)
    if not valid:
        raise RelayError(401, "Native provider authentication failed.")
    if provider not in ("twilio-sms", "twilio-voice"):
        if media_type != "application/json":
            raise RelayError(400, "Send a JSON provider webhook.")
        data = bounded_json(body)
        if provider == "clickatell":
            number = normalize_phone(config.bindings.get("clickatell-number"))
            if not number:
                raise RelayError(503, "Configure the provider receiving number.")
            if normalize_phone(data.get("toNumber")) != number:
                raise RelayError(401, "Webhook does not belong to the configured number.")
        if provider == "whatsapp":
            phone_id = config.bindings.get("whatsapp-number")
            if not phone_id:
                raise RelayError(503, "Configure the provider phone number ID.")
            try:
                if data["object"] != "whatsapp_business_account" or not isinstance(data["entry"], list) or len(data["entry"]) > 10:
                    raise ValueError()
                for entry in data["entry"]:
                    if not isinstance(entry["changes"], list) or len(entry["changes"]) > 10:
                        raise ValueError()
                    for change in entry["changes"]:
                        if not isinstance(change, dict):
                            raise ValueError()
                        if change.get("field") == "messages" and change["value"]["metadata"]["phone_number_id"] != phone_id:
                            raise RelayError(401, "Webhook does not belong to the configured phone number.")
            except (KeyError, TypeError, ValueError):
                raise RelayError(400, "Invalid provider object shape.") from None
    return supplied


def ingress_headers(config, provider, body, content_type, native_signature, now=None, nonce=None):
    timestamp = str(int(time.time()) if now is None else now)
    nonce = uuid.uuid4().hex if nonce is None else nonce
    canonical = timestamp + "." + nonce + "." + provider + "." + config.public_url(provider) + "." + body
    signature = "sha256=" + hmac.new(config.ingress_secret.encode(), canonical.encode(), hashlib.sha256).hexdigest()
    # Only the already verified native authentication field survives. Caller Host, forwarded
    # headers, credentials, cookies and caller-supplied ingress signatures are never forwarded.
    return {"Content-Type": content_type, NATIVE_HEADERS[provider]: native_signature,
            "X-CivicOps-Timestamp": timestamp, "X-CivicOps-Nonce": nonce, "X-CivicOps-Signature": signature}


class RelayServer(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 16

    def __init__(self, config, port=None, request_deadline_seconds=15):
        self.config = config
        self.slots = threading.BoundedSemaphore(16)
        self.request_deadline_seconds = request_deadline_seconds
        self.deadlines = {}
        self.deadline_lock = threading.Lock()
        super().__init__(("127.0.0.1", config.port if port is None else port), RelayHandler)

    def get_request(self):
        connection, address = super().get_request()
        connection.settimeout(10)
        return connection, address

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            try:
                request.sendall(b"HTTP/1.0 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            finally:
                self.shutdown_request(request)
            return
        # Socket timeouts alone are idle limits. An absolute deadline also stops a
        # sender who continuously trickles authenticated or unauthenticated input.
        timer = threading.Timer(self.request_deadline_seconds, self.expire_request, args=(request,))
        timer.daemon = True
        with self.deadline_lock:
            self.deadlines[request] = timer
        timer.start()
        try:
            super().process_request(request, client_address)
        except Exception:
            self.slots.release()
            self.shutdown_request(request)
            raise

    @staticmethod
    def expire_request(request):
        try:
            request.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass

    def shutdown_request(self, request):
        with self.deadline_lock:
            timer = self.deadlines.pop(request, None)
        if timer is not None:
            timer.cancel()
        super().shutdown_request(request)

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()

    def handle_error(self, request, client_address):
        # Default traceback can contain resident input; this gateway deliberately has no PII log.
        pass


class RelayHandler(BaseHTTPRequestHandler):
    server_version = "CivicOpsChannelRelay"

    def log_message(self, *_):
        pass

    def send_error(self, code, message=None, explain=None):
        # The base implementation includes the raw request method/path in its error response.
        self.respond(code, b'{"error":"Invalid relay request."}', "application/json")

    def respond(self, status, body, content_type):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)
        self.close_connection = True

    def route(self, allow_query=False):
        parsed = urlsplit(self.path)
        prefix = self.server.config.path_prefix
        if parsed.scheme or parsed.netloc or parsed.fragment or not parsed.path.startswith(prefix):
            raise RelayError(404, "Unknown relay route.")
        provider = parsed.path[len(prefix):]
        if provider not in PROVIDERS or parsed.query and not allow_query:
            raise RelayError(404, "Unknown relay route.")
        if len(self.headers) > 64 or sum(len(k) + len(v) for k, v in self.headers.items()) > 32768:
            raise RelayError(431, "Relay headers exceed their bound.")
        return provider, parsed

    def forward(self, method, path, body=None, headers=None):
        config = self.server.config
        connection = http.client.HTTPConnection(config.target_host, config.target_port, timeout=10)
        try:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            response_body = response.read(MAX_BODY + 1)
            if len(response_body) > MAX_BODY or 300 <= response.status < 400:
                raise RelayError(502, "Local CivicOps response is invalid; reconcile before retrying.")
            content_type = response.getheader("Content-Type", "application/json")
            if content_type.split(";", 1)[0].strip() not in ("application/json", "application/xml", "text/xml", "text/plain"):
                content_type = "application/octet-stream"
            self.respond(response.status, response_body, content_type)
        except (OSError, http.client.HTTPException):
            raise RelayError(502, "Local CivicOps is unavailable; reconcile before retrying.") from None
        finally:
            connection.close()

    def do_POST(self):
        try:
            provider, parsed = self.route()
            headers = list(self.headers.items())
            length = single_header(headers, "Content-Length")
            if self.headers.get("Transfer-Encoding") or not re.fullmatch(r"[0-9]{1,7}", length):
                raise RelayError(400, "Provide one bounded Content-Length; transfer encoding is disabled.")
            count = int(length)
            if not 0 < count <= MAX_BODY:
                raise RelayError(413, "Provider body exceeds its bound.")
            raw = self.rfile.read(count)
            if len(raw) != count:
                raise RelayError(400, "Incomplete provider body.")
            try:
                body = raw.decode("utf-8", "strict")
            except UnicodeError:
                raise RelayError(400, "Provider body must be UTF-8.") from None
            content_type = single_header(headers, "Content-Type")
            native = verify_native(self.server.config, provider, body, content_type, headers)
            signed_headers = ingress_headers(self.server.config, provider, body, content_type, native)
            self.forward("POST", parsed.path, raw, signed_headers)
        except RelayError as error:
            self.respond(error.status, json.dumps({"error": str(error)}).encode(), "application/json")
        except (OSError, ValueError):
            self.respond(400, b'{"error":"Invalid relay request."}', "application/json")

    def do_GET(self):
        try:
            provider, parsed = self.route(allow_query=True)
            if provider != "whatsapp":
                raise RelayError(404, "Unknown relay route.")
            if len(parsed.query) > 2048:
                raise RelayError(400, "Invalid webhook challenge.")
            pairs = parse_qsl(parsed.query, keep_blank_values=True, max_num_fields=3, errors="strict")
            values = dict(pairs)
            if len(values) != len(pairs) or set(values) != {"hub.mode", "hub.verify_token", "hub.challenge"}:
                raise RelayError(400, "Invalid webhook challenge.")
            if (values["hub.mode"] != "subscribe" or not re.fullmatch(r"[0-9]{1,256}", values["hub.challenge"])
                    or not equal_secret(self.server.config.secrets.get("whatsapp-verify"), values["hub.verify_token"])):
                raise RelayError(401, "Webhook challenge authentication failed.")
            self.forward("GET", parsed.path + "?" + parsed.query)
        except RelayError as error:
            self.respond(error.status, json.dumps({"error": str(error)}).encode(), "application/json")
        except (OSError, ValueError, UnicodeError):
            self.respond(400, b'{"error":"Invalid webhook challenge."}', "application/json")


def main():
    try:
        config = RelayConfig.from_environ()
        with RelayServer(config) as server:
            print(f"CivicOps authenticated channel relay: loopback port {config.port}; owner HTTPS ingress required.", flush=True)
            server.serve_forever()
    except RelayError as error:
        raise SystemExit(str(error)) from None
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
