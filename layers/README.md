# CivicOps layers

Optional product layers belong here. The municipal core publishes versioned catalogue, qualification and training contracts instead of requiring a layer to edit incident storage or dispatcher workflows.

Start with [`docs/EXTENSIONS.md`](../docs/EXTENSIONS.md) and [`docs/schemas/`](../docs/schemas/). Use a separately scoped training client and the training SSE stream. The dispatcher stream exposes the same training event names for dashboard refresh. No layer may acquire incident write authority through a training credential.

[`training/`](training/README.md) is a text-only placeholder for a future VR crew-training layer. Rendering, headset integration and scene code are deliberately absent from this build.
