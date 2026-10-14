# Future crew-training layer

This directory reserves the extension boundary for a future municipal electrician, plumber, road-works or emergency-services training layer. No VR, 3D, IWSDK or WebXR implementation is included.

The layer reads `/api/v1/training/work-order-types`, `/skills`, `/certification-types` and `/workers/{id}/qualifications`, then maps stable catalogue codes to its own modules. It sends bounded, idempotent `/completions` and `/certifications` requests through a trusted backend with a scoped `CIVICOPS_TRAINING_CLIENTS` credential. Authenticate supervisor attestations in that backend; the core currently records the client's sign-off assertion. Never grant external certificates from simulation or embed core tokens in browser/headset bundles.

Listen for `training.completed` and `certification.granted` on `/api/v1/training/stream` and refresh permitted records after reconnect. Events contain `{}` and carry no resident or worker details. Keep evidence outside the core and submit only its SHA-256 digest.

The exact contract, policy settings, trust limits and persistence behavior are in [`docs/EXTENSIONS.md`](../../docs/EXTENSIONS.md); portable JSON Schemas are in [`docs/schemas/`](../../docs/schemas/).
