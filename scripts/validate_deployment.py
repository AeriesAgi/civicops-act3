"""Semantic checks for assets which cannot run on this host (no Docker daemon or cluster)."""
import pathlib
import sys
import yaml

root = pathlib.Path(__file__).resolve().parent.parent
evidence = pathlib.Path(sys.argv[1])
compose = yaml.safe_load((root / "deploy/compose/docker-compose.yml").read_text())
services = compose["services"]
assert set(services) == {"civicops", "postgres", "otel-collector", "amd-vllm"}
app = services["civicops"]
assert app["read_only"] and app["cap_drop"] == ["ALL"] and app["env_file"]
assert app["depends_on"]["postgres"]["condition"] == "service_healthy"
assert app["ports"][0].startswith("127.0.0.1:")
assert "ports" not in services["postgres"] and "POSTGRES_HOST_AUTH_METHOD" not in services["postgres"]["environment"]
assert services["postgres"]["image"] == "postgres:17.11-bookworm"
assert services["otel-collector"]["profiles"] == ["observability"]
assert services["amd-vllm"]["profiles"] == ["amd-vllm"]
assert services["amd-vllm"]["devices"] == ["/dev/kfd:/dev/kfd", "/dev/dri:/dev/dri"]
assert "ports" not in services["amd-vllm"]
assert compose["networks"]["backend"]["internal"]
assert yaml.safe_load((root / "deploy/compose/otel-collector.yml").read_text())["exporters"]["prometheus"]["endpoint"] == "0.0.0.0:8889"
for variant in ("postgres", "file", "features", "amd"):
    resources = [item for item in yaml.safe_load_all((evidence / f"{variant}.yaml").read_text()) if item]
    deployments = [item for item in resources if item["kind"] == "Deployment"]
    civicops = next(item for item in deployments if item["metadata"]["name"] == "civicops-civicops")
    spec = civicops["spec"]
    assert spec["replicas"] == 1 and spec["strategy"]["type"] == "Recreate"
    pod = spec["template"]["spec"]
    assert pod["automountServiceAccountToken"] is False and pod["securityContext"]["runAsNonRoot"]
    assert pod["securityContext"]["runAsUser"] == 1654
    container = pod["containers"][0]
    fixed_env = {entry["name"]: entry["value"] for entry in container["env"]}
    assert fixed_env["CIVICOPS_STORAGE"] == ("file" if variant == "file" else "postgres")
    assert fixed_env["CIVICOPS_DATA_DIR"] == "/app/Data" and fixed_env["ASPNETCORE_HTTP_PORTS"] == "8080"
    assert container["securityContext"]["readOnlyRootFilesystem"]
    assert container["securityContext"]["allowPrivilegeEscalation"] is False
    assert container["readinessProbe"]["httpGet"]["path"] == "/health/ready"
    assert container["livenessProbe"]["httpGet"]["path"] == "/health/live"
    assert "requests" in container["resources"] and "limits" in container["resources"]
    service = next(item for item in resources if item["kind"] == "Service" and item["metadata"]["name"] == "civicops-civicops")
    assert service["spec"]["selector"]["civicops.io/component"] == "app"
    for job in (item for item in resources if item["kind"] == "Job"):
        assert job["metadata"]["annotations"]["helm.sh/hook"] == "pre-install,pre-upgrade"
        assert job["spec"]["template"]["metadata"]["labels"]["civicops.io/component"] == "migration"
        assert job["spec"]["template"]["spec"]["containers"][0]["command"] == ["dotnet", "CivicOps.dll", "--migrate"]
        policy = next(item for item in resources if item["kind"] == "NetworkPolicy" and item["metadata"]["name"].endswith("-migration"))
        assert policy["metadata"]["annotations"]["helm.sh/hook-weight"] == "-10"
        assert policy["metadata"]["annotations"]["helm.sh/hook-delete-policy"] == "before-hook-creation"
        assert policy["spec"]["podSelector"]["matchLabels"]["civicops.io/component"] == "migration"
        assert policy["spec"]["ingress"] == []
        assert len(policy["spec"]["egress"]) == 2
    if variant == "features":
        ingress = next(item for item in resources if item["kind"] == "Ingress")
        assert ingress["spec"]["tls"][0]["secretName"]
        monitor = next(item for item in resources if item["kind"] == "ServiceMonitor")
        assert monitor["spec"]["selector"]["matchLabels"]["app.kubernetes.io/name"] == "otel-collector"
        assert next(item for item in resources if item["kind"] == "HorizontalPodAutoscaler")["spec"]["maxReplicas"] == 1
    if variant == "amd":
        model = next(item for item in deployments if item["metadata"]["name"] == "civicops-vllm")
        assert model["spec"]["template"]["spec"]["containers"][0]["resources"]["limits"]["amd.com/gpu"] == 1
        assert model["spec"]["template"]["spec"]["securityContext"]["runAsNonRoot"]
    if variant == "file":
        assert any(item["kind"] == "PersistentVolumeClaim" for item in resources)
print("Compose and four rendered variants passed storage, probes, security, Secret references, migration selectors, metrics target and GPU checks.")
