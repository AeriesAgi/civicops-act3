# What happens after a report

1. A report becomes a case and the assistant repeats its reference.
2. Backend agents structure the intake, recommend triage, flag duplicates and propose an eligible crew. Every AI recommendation records its provider, model, latency and fallback status.
3. Zendesk holds a linked ticket or a clearly labelled simulated ticket request.
4. A human dispatcher approves the case and then assigns an eligible crew.
5. Human-reviewed updates explain status changes. We do not guarantee response or arrival times.
6. Every change is written to a hash-chained audit trail.

Keep the reference to look up the minimal public status. Ask for human review if the information is uncertain. A callback requires explicit recorded consent and a dispatcher action.
