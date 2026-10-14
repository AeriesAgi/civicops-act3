#!/usr/bin/env python3
"""Benchmark CivicOps' production triage prompt on an operator-provided vLLM endpoint.

Standard library only. Never fabricates AMD results. Run against synthetic cases only.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import time
from datetime import datetime, timezone
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit, urlunsplit
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
DANGER_TERMS = ("fire", "trapped", "live wire", "exposed wire", "life threatening", "electrocution",
                "flooding home", "flooding inside", "flooding my house", "flooding my home")


def safe_url(value):
    parsed = urlsplit(value)
    if (parsed.scheme not in ("http", "https") or not parsed.netloc or parsed.username
            or parsed.password or parsed.query or parsed.fragment):
        raise ValueError("Endpoint must be HTTP(S), without embedded credentials, query or fragment")
    return parsed


def completion_url(base):
    parsed = safe_url(base.strip())
    path = parsed.path.rstrip("/")
    path = path if path.endswith("/v1") else path + "/v1"
    return urlunsplit((parsed.scheme, parsed.netloc, path + "/chat/completions", "", ""))


def unique_object(pairs):
    result = {}
    for name, value in pairs:
        if name in result:
            raise ValueError("duplicate_json_key")
        result[name] = value
    return result


def fetch_json(url, payload=None, api_key=None, timeout=10):
    headers = {"Accept": "application/json"}
    if payload is not None:
        headers["Content-Type"] = "application/json"
    if api_key:
        headers["Authorization"] = "Bearer " + api_key
    request = Request(url, data=None if payload is None else json.dumps(payload).encode(), headers=headers)
    with urlopen(request, timeout=timeout) as response:
        body = response.read(65_537)
        if len(body) > 65_536:
            raise ValueError("response_too_large")
        return json.loads(body, object_pairs_hook=unique_object)


def valid_schema(value, schema):
    kind = schema.get("type")
    if kind == "object":
        if not isinstance(value, dict):
            return False
        properties = schema.get("properties", {})
        if any(key not in value for key in schema.get("required", [])):
            return False
        if schema.get("additionalProperties") is False and set(value) - set(properties):
            return False
        return all(valid_schema(item, properties[key]) for key, item in value.items() if key in properties)
    if kind == "string":
        return (isinstance(value, str) and schema.get("minLength", 0) <= len(value) <= schema.get("maxLength", math.inf)
                and ("enum" not in schema or value in schema["enum"]) and all(ord(char) >= 32 or char in "\r\n\t" for char in value))
    if kind == "number":
        return (isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)
                and schema.get("minimum", -math.inf) <= value <= schema.get("maximum", math.inf))
    if kind == "boolean":
        return isinstance(value, bool)
    if kind == "array":
        return (isinstance(value, list) and len(value) <= schema.get("maxItems", math.inf)
                and all(valid_schema(item, schema["items"]) for item in value))
    return False


def valid_triage(value, schema, work_orders):
    if not valid_schema(value, schema):
        return False
    work_order = work_orders.get(value.get("workOrderTypeCode"))
    return (work_order is not None and value.get("category") == work_order["category"]
            and value.get("department") == work_order["department"])


def safety_floor(output, description, work_orders):
    safe_output = dict(output)
    text = description.lower()
    if any(term in text for term in DANGER_TERMS):
        safe_output["priority"] = "Urgent"
    critical_code = ("ELEC.LIVE_WIRE_DOWN" if any(term in text for term in ("live wire", "exposed wire", "electrocution", "lewendige draad"))
                     else "EMERG.FLOOD_RESPONSE" if any(term in text for term in
                          ("flood response", "flooding home", "flooding my home", "flooding inside", "flooding my house", "huis oorstroom"))
                     else None)
    if critical_code:
        work_order = work_orders[critical_code]
        safe_output.update(workOrderTypeCode=critical_code, category=work_order["category"], department=work_order["department"], priority="Urgent")
    return safe_output


def percentile(values, fraction):
    values = sorted(values)
    return values[max(0, math.ceil(len(values) * fraction) - 1)] if values else 0


def request_body(contract, case, model):
    hazards = [term for term in DANGER_TERMS if term in case["description"].lower()][:8]
    triage_input = {"description": case["description"], "area": "Synthetic Ward", "hazards": hazards, "serviceSignal": None}
    return {
        "model": model,
        "messages": [{"role": "system", "content": contract["systemPrompt"]},
                     {"role": "user", "content": json.dumps(triage_input, separators=(",", ":"))}],
        "temperature": 0, "max_tokens": 800,
        "response_format": {"type": "json_schema", "json_schema": {"name": "TriageAgent", "strict": True, "schema": contract["schema"]}},
        "chat_template_kwargs": {"enable_thinking": False},
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.getenv("CIVICOPS_LLM_BASE_URL"))
    parser.add_argument("--civicops-url", default=os.getenv("CIVICOPS_BASE_URL", "http://localhost:5080"))
    parser.add_argument("--model", default=os.getenv("CIVICOPS_LLM_MODEL", "Qwen/Qwen3-8B"))
    parser.add_argument("--provider-label", default=os.getenv("CIVICOPS_LLM_PROVIDER_LABEL", "hardware unverified"))
    parser.add_argument("--contract-file", type=Path, help="Optional previously exported production contract")
    parser.add_argument("--dataset", type=Path, default=ROOT / "deploy/amd/triage_cases.json")
    parser.add_argument("--output", type=Path, default=ROOT / "reports/amd_benchmark.md")
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--warmup", type=int, default=1)
    parser.add_argument("--timeout-ms", type=int, default=int(os.getenv("CIVICOPS_LLM_TIMEOUT_MS", "10000")))
    parser.add_argument("--dry-run", action="store_true", help="Print one exact request without calling the model")
    args = parser.parse_args()
    if not args.base_url:
        parser.error("Provide CIVICOPS_LLM_BASE_URL or --base-url. No AMD measurement exists without an endpoint.")
    if not 1 <= args.repetitions <= 50 or not 0 <= args.warmup <= 10 or not 250 <= args.timeout_ms <= 60_000:
        parser.error("repetitions 1..50, warmup 0..10, timeout-ms 250..60000")
    try:
        endpoint = completion_url(args.base_url)
        civicops = safe_url(args.civicops_url)
        contract = (json.loads(args.contract_file.read_text()) if args.contract_file
                    else fetch_json(args.civicops_url.rstrip("/") + "/api/ai/triage-contract"))
        if not isinstance(contract.get("systemPrompt"), str) or not isinstance(contract.get("schema"), dict):
            raise ValueError("invalid_production_contract")
        catalogue = json.loads((ROOT / "products/civicops/catalog/work-orders.v1.json").read_text())
        work_orders = {item["code"]: item for item in catalogue["workOrderTypes"]}
        cases = json.loads(args.dataset.read_text())
        if (not isinstance(cases, list) or not 1 <= len(cases) <= 100
                or any(case.get("workOrderTypeCode") not in work_orders for case in cases)):
            raise ValueError("invalid_dataset")
    except (ValueError, OSError, HTTPError, URLError, KeyError):
        print("Could not load a safe endpoint, production contract, or synthetic dataset.", file=sys.stderr)
        return 2
    if args.dry_run:
        print(json.dumps(request_body(contract, cases[0], args.model), indent=2))
        return 0
    api_key = os.getenv("CIVICOPS_LLM_API_KEY")
    timeout = args.timeout_ms / 1000
    for _ in range(args.warmup):
        try:
            fetch_json(endpoint, request_body(contract, cases[0], args.model), api_key, timeout)
        except (ValueError, OSError, HTTPError, URLError):
            print("Warmup failed; proceeding with measured attempts.", file=sys.stderr)
    rows = []
    for _ in range(args.repetitions):
        for case in cases:
            start = time.perf_counter()
            valid = raw_agreement = safe_agreement = False
            completion_tokens = 0
            error = ""
            actual = "invalid"
            try:
                envelope = fetch_json(endpoint, request_body(contract, case, args.model), api_key, timeout)
                choices = envelope["choices"]
                if len(choices) != 1 or choices[0].get("finish_reason") == "length":
                    raise ValueError("invalid_or_truncated_response")
                output = json.loads(choices[0]["message"]["content"], object_pairs_hook=unique_object)
                valid = valid_triage(output, contract["schema"], work_orders)
                if valid:
                    actual = output["workOrderTypeCode"] + "/" + output["priority"]
                    labels = ("workOrderTypeCode", "category", "department", "priority")
                    raw_agreement = all(output[key] == case[key] for key in labels)
                    safe_output = safety_floor(output, case["description"], work_orders)
                    safe_agreement = all(safe_output[key] == case[key] for key in labels)
                tokens = envelope.get("usage", {}).get("completion_tokens", 0)
                completion_tokens = tokens if isinstance(tokens, int) and not isinstance(tokens, bool) and tokens >= 0 else 0
            except HTTPError as exception:
                error = "http_" + str(exception.code)
            except (ValueError, OSError, URLError, KeyError, IndexError, TypeError):
                error = "invalid_response_or_timeout"
            rows.append({"id": case["id"], "latency": (time.perf_counter() - start) * 1000, "valid": valid,
                         "raw": raw_agreement, "safe": safe_agreement, "tokens": completion_tokens, "error": error, "actual": actual})
    latencies = [row["latency"] for row in rows]
    total_seconds = sum(latencies) / 1000
    parsed = urlsplit(endpoint)
    endpoint_label = f"{parsed.scheme}://{parsed.hostname}:{parsed.port or (443 if parsed.scheme == 'https' else 80)}"
    contract_hash = hashlib.sha256(json.dumps(contract, sort_keys=True).encode()).hexdigest()
    count = len(rows)
    lines = ["# CivicOps AMD triage benchmark", "", "Measured: " + datetime.now(timezone.utc).isoformat(),
             "", f"Model: `{args.model.replace('`', '')}`", f"Provider label: {args.provider_label.replace(chr(10), ' ')}",
             f"Endpoint label: `{endpoint_label}`", f"Production contract SHA-256: `{contract_hash}`", f"Work-order catalogue version: `{catalogue['version']}`", "",
             "Hardware is operator-declared. Attach AMD Instinct host / ROCm evidence before claiming AMD results.", "",
             f"Synthetic cases: {len(cases)}; repetitions: {args.repetitions}; excluded warmup attempts: {args.warmup}.", "",
             "| Metric | Measured result |", "|---|---:|",
             f"| p50 end-to-end request latency | {percentile(latencies, .5):.2f} ms |",
             f"| p95 end-to-end request latency | {percentile(latencies, .95):.2f} ms |",
             f"| Completion tokens / total measured seconds | {sum(row['tokens'] for row in rows) / total_seconds if total_seconds else 0:.2f} |",
             f"| Strict JSON/schema validity | {sum(row['valid'] for row in rows) / count:.1%} |",
             f"| Raw model label agreement | {sum(row['raw'] for row in rows) / count:.1%} |",
             f"| Agreement after CivicOps danger floor | {sum(row['safe'] for row in rows) / count:.1%} |",
             f"| Failed requests | {sum(bool(row['error']) for row in rows)} |", "",
             "Tokens/s is completion-token count divided by complete request wall time; it is not GPU-only decode throughput. "
             "No automatic retry is used in the benchmark. Application calls have a shared timeout and two-attempt maximum.", "",
             "| Case | Latency ms | JSON valid | Model labels | Raw agreement | Safety-floor agreement | Error |",
             "|---|---:|---|---|---|---|---|"]
    lines.extend(f"| {row['id']} | {row['latency']:.2f} | {row['valid']} | {row['actual']} | {row['raw']} | {row['safe']} | {row['error']} |" for row in rows)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(lines) + "\n")
    print(f"{count} measured requests; JSON validity {sum(row['valid'] for row in rows)}/{count}; report: {args.output}")
    return 0 if all(row["valid"] for row in rows) else 1


if __name__ == "__main__":
    sys.exit(main())
