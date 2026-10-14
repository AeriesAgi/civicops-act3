#!/usr/bin/env python3
"""Round 13 multi-agency smoke against a running synthetic demo. All data synthetic."""
import json, os, sys, urllib.request, urllib.error

BASE = os.environ["CIVICOPS_BROWSER_BASE_URL"].rstrip("/")

def client(role):
    # Session cookies are Secure; the loopback smoke is plain HTTP, so carry them explicitly.
    opener = urllib.request.build_opener(); state = {"csrf": None, "cookies": {}}
    def call(method, path, body=None):
        headers = {"Content-Type": "application/json"}
        if state["csrf"]: headers["X-CivicOps-CSRF"] = state["csrf"]
        if state["cookies"]: headers["Cookie"] = "; ".join(f"{k}={v}" for k, v in state["cookies"].items())
        req = urllib.request.Request(BASE + path, method=method, headers=headers, data=None if body is None else json.dumps(body).encode())
        try:
            with opener.open(req) as r:
                keep(r.headers.get_all("Set-Cookie") or []); return r.status, json.loads(r.read() or b"null")
        except urllib.error.HTTPError as e: keep(e.headers.get_all("Set-Cookie") or []); return e.code, None
    def keep(values):
        for value in values:
            name, _, rest = value.partition("="); state["cookies"][name.strip()] = rest.split(";")[0]
    state["csrf"] = call("GET", "/auth/status")[1]["csrfToken"]
    status, _ = call("POST", "/auth/demo-login", {"role": role, "tenantId": "default"}); assert status == 200, status
    state["csrf"] = call("GET", "/auth/status")[1]["csrfToken"]
    return call

def check(cond, label):
    print(("PASS " if cond else "FAIL ") + label)
    if not cond: sys.exit(1)

d, s = client("dispatcher"), client("supervisor")
st, agencies = d("GET", "/api/v1/agencies"); check(st == 200 and len(agencies) == 9, "nine agencies listed")
st, inc = d("POST", "/api/v1/agency-incidents", {"operationId": "smoke-crash", "title": "Synthetic crash with downed pole", "typeCode": "TRAFFIC.CRASH_SCENE",
    "agencies": ["ems", "fire", "electricity"], "summary": "Synthetic smoke scenario.", "location": {"longitude": 31.02, "latitude": -29.86}, "addressOrLandmark": "Synthetic Rd"})
check(st == 200 and len(inc["agencies"]) == 4, "cross-agency incident created")
iid = inc["id"]
st, inc = s("POST", f"/api/v1/agency-incidents/{iid}/commander", {"operationId": "smoke-cmd", "commander": "Synthetic Officer", "icsRole": "Incident Commander", "note": "Smoke."})
check(st == 200 and inc["status"] == "Active", "commander assigned by supervisor")
st, inc = d("POST", f"/api/v1/agency-incidents/{iid}/mutual-aid", {"operationId": "smoke-aid", "fromAgency": "fire", "toAgency": "fire", "toTenantId": "neighbour-metro",
    "resources": "One rescue pumper", "scope": "Summary", "note": "Smoke."})
aid = inc["mutualAid"][0]["id"]
st, _ = d("POST", f"/api/v1/agency-incidents/{iid}/mutual-aid/{aid}/decision", {"operationId": "smoke-dec-d", "accept": True, "note": "Smoke."})
check(st == 403, f"dispatcher cannot decide mutual aid ({st})")
st, inc = s("POST", f"/api/v1/agency-incidents/{iid}/mutual-aid/{aid}/decision", {"operationId": "smoke-dec", "accept": True, "note": "Smoke human acceptance."})
check(st == 200 and inc["mutualAid"][0]["status"] == "Accepted", "supervisor accepted mutual aid")
st, _ = d("POST", "/api/v1/detections", {"operationId": "smoke-bad", "eventType": "face_match", "confidence": 0.9, "cameraId": "SIM-CAM-01",
    "location": {"longitude": 31.0, "latitude": -29.9}, "observedAt": "2026-10-13T12:00:00Z"})
check(st == 400, "non-allow-listed detection rejected")
st, _ = d("POST", "/api/v1/detections", {"operationId": "smoke-img", "eventType": "crash", "confidence": 0.9, "cameraId": "SIM-CAM-01",
    "location": {"longitude": 31.0, "latitude": -29.9}, "observedAt": "2026-10-13T12:00:00Z", "image": "x"})
check(st == 400, "imagery field rejected")
before = {e["id"] for e in d("GET", "/api/v1/detections")[1]["events"]}
st, feed = d("POST", "/api/v1/detections/simulate", {"operationId": "smoke-sim", "seed": 31, "count": 6})
fresh = [e for e in feed["events"] if e["id"] not in before]
check(st == 200 and len(fresh) == 6 and all(e["status"] == "Proposed" for e in fresh), "simulated feed proposes only")
st, det = d("POST", f"/api/v1/detections/{fresh[0]['id']}/confirm", {"operationId": "smoke-confirm", "note": "Operator verified."})
check(st == 200 and det["incidentId"], "human confirmation creates incident")
st, board = d("GET", "/api/v1/agency-incidents"); check(st == 200 and len(board["incidents"]) >= 2, "board shows the new incidents")
print("agency smoke complete")
