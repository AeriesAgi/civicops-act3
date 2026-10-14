-- PostgreSQL is authoritative: paths are logical identifiers, never mirrored files.
CREATE TABLE civicops_documents (
    path text PRIMARY KEY,
    entity_kind text NOT NULL,
    tenant_id text NOT NULL,
    payload text NOT NULL,
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX civicops_documents_tenant_kind ON civicops_documents (tenant_id, entity_kind);
COMMENT ON TABLE civicops_documents IS 'Versioned durable snapshots: tenants, workforce/crews/workers, training, service accounts, bootstrap users, catalogue, consent/contact/DSAR, provider receipts and simulated delivery artifacts.';
CREATE TABLE civicops_incidents (
    tenant_id text NOT NULL,
    id text NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    intake_key_hash text,
    body jsonb NOT NULL,
    PRIMARY KEY (tenant_id, id)
);
CREATE UNIQUE INDEX civicops_incidents_intake_key ON civicops_incidents (tenant_id, intake_key_hash) WHERE intake_key_hash IS NOT NULL;
CREATE TABLE civicops_audit (
    tenant_id text NOT NULL,
    sequence bigint NOT NULL,
    hash text NOT NULL,
    previous_hash text NOT NULL,
    body jsonb NOT NULL,
    PRIMARY KEY (tenant_id, sequence),
    UNIQUE (tenant_id, hash)
);
CREATE TABLE civicops_outbox (
    tenant_id text NOT NULL,
    id text NOT NULL,
    deduplication_key text NOT NULL,
    state text NOT NULL,
    next_attempt_at timestamptz NOT NULL,
    body jsonb NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, deduplication_key)
);
CREATE INDEX civicops_outbox_due ON civicops_outbox (tenant_id, state, next_attempt_at);
