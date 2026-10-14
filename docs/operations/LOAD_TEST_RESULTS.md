# Bounded synthetic load testing

These tests exercise CivicOps on loopback in **EXPLICIT DEMO**, using synthetic reports/workers, deterministic reasoning, simulated ElevenLabs/Zendesk delivery and email outboxes. No GPU, live provider, identity issuer or external inference endpoint is contacted. Results on the shared 8-core / 15 GiB VM are regression evidence for this small workload, **not municipal capacity, an achieved SLO, or AMD performance**. Dataset growth and complete-snapshot verification/serialization limit scaling; measure the municipality's expected retained dataset before a pilot.

Run from the repository after installing .NET 10 SDK, PostgreSQL 17 binaries, Grafana k6 2.3.0 and local Chrome/Chromium:

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TMPDIR=/workspace/civicops/tmp
export DOTNET_PROCESSOR_COUNT=2
./scripts/loadtest.sh
```

The default command builds Release, runs the file store, and invokes `with_ephemeral_pg.sh` to run PostgreSQL 17 as the current user on a random loopback port. `./scripts/loadtest.sh file` runs only files; `./scripts/with_ephemeral_pg.sh ./scripts/loadtest.sh postgres` runs only the disposable database. A direct `postgres` invocation refuses to use an owner database. Missing k6/Chrome fails with an actionable message; no cloud load service is used.

Optional `CIVICOPS_LOADTEST_RESULTS` selects the output directory (default a new directory under `$TMPDIR`). `CIVICOPS_CHROME_PATH` selects local Chromium. The harness owns `CIVICOPS_LOADTEST_BASE_URL` and `CIVICOPS_LOADTEST_SUMMARY`; direct k6 execution only accepts a loopback HTTP origin. Existing quotas are raised to 10,000 only inside the disposable workload so this measures storage/application latency. Production quota effectiveness is covered separately by the request-security tests; this workload does not measure distributed rate limiting.

Each backend starts with 15 seeded synthetic cases, and the harness waits for their simulated deliveries to finish before measuring new work. Scenarios run sequentially, use at most five API VUs or three Chromium SSE VUs, and have bounded durations (at most 15 seconds per scenario, 49 seconds total configured maximum per backend including graceful-stop allowance). Startup/build/warmup time is separate. New reports still trigger the real hosted outbox worker during measurement. The PostgreSQL fixture caps shared buffers at 32 MB and connections at 30; the application pool is five.

| Scenario | Workload | Thresholds |
|---|---|---|
| Resident intake burst | 3 VUs, 20 reports, initial-VU idempotent retries, maximum 15s | All checks pass; HTTP failure rate 0; p95 <2,500ms; exactly 20 iterations |
| Dispatcher queue/metrics | 5 VUs, 8s plus 2s graceful stop; 200ms think time | All checks pass; HTTP failure rate 0; p95 <1,000ms; at least 5 iterations |
| Training API | 3 VUs, 12 completions plus catalogue/qualification reads, maximum 12s | All checks pass; HTTP failure rate 0; p95 <2,500ms; exactly 12 iterations |
| SSE connections | 3 concurrent native Chromium EventSources; ready, actual synthetic write/refresh, 2s held open, maximum 12s | All checks pass; exactly 3 iterations; p95 first event <2,000ms and write-to-refresh <2,500ms |

SSE uses native `EventSource` through [k6/browser](https://grafana.com/docs/k6/latest/javascript-api/k6-browser/), with a local static page and explicit connection closure. This validates ready events, actual single-node refresh delivery and open connections; it does not treat a timed-out buffered HTTP request as a successful stream. There is no replay cursor or cross-node fan-out. [k6 thresholds](https://grafana.com/docs/k6/latest/using-k6/thresholds/) enforce the stated regression bounds; these bounds are local test criteria chosen for this modest workload.

`machine.json` records UTC timestamp, OS/kernel, CPU model/core count, free memory, SDK and k6 versions. Each scenario saves raw k6 metrics/threshold outcomes and logs, including the server log on failure; the final audit verification must be valid. These artifacts stay outside Git and contain only synthetic data.

## Measured local run — 8 October 2026

Exact final command, after the storage optimizations and isolation of the outbox leadership connection from the application pool:

```bash
CIVICOPS_LOADTEST_RESULTS="$TMPDIR/round7-load-leader-final" ./scripts/loadtest.sh all
```

All eight scenario threshold sets and **696 checks** passed, with **zero failed checks and zero API HTTP failures**. Each backend ended with a valid **202-entry audit chain**. Three SSE connections per backend each received `ready` and an actual refresh and remained open for the hold period. SSE browser work is reported with dedicated event metrics, separately from API HTTP samples.

Machine: shared **8 logical CPUs, Intel Xeon**, **15 GiB RAM**, no swap, Linux **6.12.94+ x86_64/glibc 2.41**, .NET SDK **10.0.401**, k6 **2.3.0** (`e088784614`), PostgreSQL **17**. Application/build processor cap: **2**. Available memory during the final runs was approximately **4.5 GiB file / 4.4 GiB PostgreSQL**. The VM was shared with another build and other verification; earlier round observations included host load averages around **14–15**, and CPU scheduling/I/O contention is uncontrolled. No GPU, live inference, WAN or production ingress was measured. Final measurements began at **16:24:46 UTC file / 16:25:10 UTC PostgreSQL**. Raw local evidence: `$TMPDIR/round7-load-leader-final/{file,postgres}`.

| Backend / scenario | Iterations | HTTP requests / successful checks | p95 latency | Actual scenario seconds |
|---|---:|---:|---:|---:|
| File resident intake | 20 | 23 / 23 | **558.95 ms** | 2.90 |
| PostgreSQL resident intake | 20 | 23 / 23 | **1,453.23 ms** | 7.22 |
| File dispatcher queue/metrics | 160 | 320 / 320 | **82.85 ms** | 8.19 |
| PostgreSQL dispatcher queue/metrics | 126 | 252 / 252 | **145.91 ms** | 8.30 |
| File training API | 12 | 37 / 36 | **65.46 ms** | 1.13 |
| PostgreSQL training API | 12 | 37 / 36 | **287.26 ms** | 2.20 |
| File SSE | 3 | 3 event checks | Ready **12.30 ms**, refresh **43.20 ms** | 4.28 |
| PostgreSQL SSE | 3 | 3 event checks | Ready **100.03 ms**, refresh **281.41 ms** | 5.44 |

The training request count includes the setup catalogue fetch, which throws if it fails. Scenario totals were **16.50s file / 23.16s PostgreSQL**, excluding application startup/build/seed/warmup; each scenario and each backend remained within the bounded workload budget. These short samples (especially three SSE sessions) are insufficient for statistically robust tail-latency or soak claims. An earlier passing bounded run before leadership-pool isolation recorded **680 checks**, file intake p95 **624.12ms** and PostgreSQL **1,908.48ms**, in `$TMPDIR/round7-load-bounded`; changes in this shared VM comparison do not establish a causal improvement.

## Failed explorations and fixes

An initial five-VU intake exploration completed all 20 reports and five retries correctly but missed the **2,500ms p95** regression threshold: **2,728.56ms** on PostgreSQL. Code review found and fixed two real N+1 paths: historical incident/audit/outbox projection commands now operate once per collection inside the original transaction; contact authentication reads/validates the municipality's shared contact snapshot once per case snapshot instead of once per case. Existing immutable-audit, rollback, tenant isolation, shared concurrency, privacy/tamper and import contracts remain green; a focused combined run passed **78 cases**. Advisory worker leadership also prevents redundant hosted polling across PostgreSQL peers.

Repeated five-VU explorations after the changes still missed the same threshold, around **2.81–3.0s p95**. The shared VM data therefore does **not demonstrate a causal latency improvement** from these changes, and five-VU intake is **not reported as passing**. Settling the seeded outbox did not alone resolve that miss. The committed modest regression profile uses **three intake VUs** while retaining all 20 reports, idempotent retries, the hosted worker and the unchanged **2,500ms threshold**; dispatcher reads retain five VUs. Its passing result only establishes that exact bounded workload. Do not extrapolate a municipal concurrency limit from it.

The remaining coarse database lock, complete snapshots and repeated full audit hashing impose scaling limits. A row-based repository, narrower locks, larger retained-dataset measurements and an isolated production-like machine are future engineering/owner work. No load-related audit, idempotency or authorization failure was hidden by changing thresholds.

Owner-pending: production-like retained datasets, concurrent municipalities, provider latency/failures, TLS/ingress, real PostgreSQL infrastructure, distributed sessions/quotas/SSE, soak/recovery under load, desired municipal arrival rates, and AMD inference cost/throughput. No claim about these follows from this loopback test.

## Round-8 consistency rerun — 8 October 2026 SAST

`CIVICOPS_LOADTEST_RESULTS="$TMPDIR/round8/load" ./scripts/loadtest.sh all` reran the unchanged bounded profiles on both backends: **all 8 threshold sets / 652 checks passed**, zero failed checks/API HTTP failures, **202 verified audit entries per backend**. File began at **19:29:32 SAST**, PostgreSQL at **19:29:58 SAST**. Intake p95 was **451.44 / 1,718.86 ms** (file/PostgreSQL), queue p95 **132.76 / 134.47 ms**, training p95 **92.10 / 280.76 ms**, and actual write-to-SSE refresh p95 **48.78 / 142.63 ms**. API iteration counts were 20 intake and 12 training each; queue iterations were 147 file / 117 PostgreSQL; SSE used three browser sessions per backend.

The variation in queue checks follows its fixed-duration workload, not removed assertions or relaxed thresholds. The same shared-VM, processor cap of two, loopback/synthetic, no-GPU/provider and short-tail-sample limitations apply. No change to the load implementation was needed in round 8, and the failed five-VU explorations above remain disclosed. The [complete final verification](../VERIFICATION.md) records the broader tests and operating boundaries.
