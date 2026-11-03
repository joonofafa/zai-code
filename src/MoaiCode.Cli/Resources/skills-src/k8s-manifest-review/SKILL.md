---
name: k8s-manifest-review
description: Use when writing or reviewing Kubernetes/OpenShift YAML, Helm values/templates, or Kustomize overlays (Deployment, StatefulSet, Service, Route/Ingress, HPA, PDB, NetworkPolicy) before they are applied — checks resources, probes, autoscaling, fault isolation, security context, and air-gapped image references.
license: MATE Builder original — no third-party content
---

# Kubernetes / OpenShift manifest review

Review the rendered YAML, not just the template. For Helm, render with `helm template <release> <chart> -f values.yaml > /tmp/rendered.yaml`. For Kustomize, use `oc kustomize <dir>` or `kubectl kustomize <dir>`. Validate against the cluster schema without changing anything: `oc apply --dry-run=server -f /tmp/rendered.yaml` and `oc diff -f /tmp/rendered.yaml`. Field documentation is available offline with `oc explain <kind>.<path>`.

Report findings as **must fix / should fix / note**, each with `file:line` (or the resource and field path), the risk, and the corrected YAML snippet.

## Checklist
### Images (air-gapped)
- The image must come from the internal registry or mirror, never `docker.io/...`, `quay.io/...`, `ghcr.io/...`, or an unqualified `nginx`, unless a mirror policy is confirmed.
- Pin by digest (`@sha256:`) or an immutable tag. Never `:latest` in production. With a mutable tag, the `imagePullPolicy` must be stated intentionally.
- `imagePullSecrets` or a service account with the pull secret is present when the registry needs auth.

### Resources
- Every container (including init and sidecars) has `resources.requests` for cpu and memory. Without requests, the scheduler and the HPA cannot work correctly.
- Memory `limits` are set and the limit is ≥ the request. JVM apps size the heap to the container (`-XX:MaxRAMPercentage=` around 60–75, or an explicit `-Xmx` below the limit with headroom for metaspace and threads).
- A CPU limit is a policy choice: limits cause throttling and latency spikes for latency-critical services. Flag it, and follow the team standard.
- GPU: `nvidia.com/gpu` is set in `limits` (requests are implied equal). Tolerations and node selectors match the GPU nodes.
- Namespace quota and LimitRange allow these values (`oc describe quota,limitrange -n <ns>`).

### Probes
- `readinessProbe` gates traffic. It checks the app's own readiness, not its downstream dependencies. If a shared DB is down, every pod goes unready and the whole service disappears.
- A `livenessProbe` that is too aggressive kills slow-starting apps. Use a `startupProbe` for long starts, and keep liveness cheap and local.
- Probe ports and paths match the container. `timeoutSeconds` is realistic for the app (the default of 1s is often too short).

### Availability and fault isolation
- `replicas` ≥ 2 for anything user-facing, with a `PodDisruptionBudget` (`minAvailable` or `maxUnavailable`) so node drains do not take the service down.
- Pods spread across nodes and zones: `topologySpreadConstraints` or pod anti-affinity.
- Rolling update settings (`maxUnavailable`, `maxSurge`) keep capacity during deploys. `terminationGracePeriodSeconds` covers in-flight requests. A `preStop` sleep of a few seconds lets endpoints drain before SIGTERM handling.
- Critical paths (for example, payment) are separate Deployments from non-critical features, so a failure or scale event in one does not take the other with it.
- HPA: `minReplicas` covers the base load; `maxReplicas` is bounded by quota and downstream capacity (DB connections × replicas ≤ the pool the DB allows). `behavior.scaleDown.stabilizationWindowSeconds` avoids flapping.

### Security
- `securityContext`: `runAsNonRoot: true`, `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `seccompProfile.type: RuntimeDefault`. On OpenShift, do not hard-code `runAsUser` unless the SCC allows it. Let `restricted-v2` assign the UID.
- No `privileged: true`, `hostNetwork`, `hostPID`, or `hostPath` mounts unless justified and approved.
- Secrets come from `Secret` objects or an external secret mechanism, never inline in env values, ConfigMaps, or committed YAML. Grep the files for `password`, `secret`, `token`, and `BEGIN .* PRIVATE KEY`.
- `automountServiceAccountToken: false` unless the pod calls the API. RBAC for the service account is the minimum needed.
- A `NetworkPolicy` exists for the namespace (default deny plus explicit allows) when the platform standard requires it.

### Exposure
- Service `selector` matches the pod template labels exactly. `targetPort` matches `containerPort` (by name, preferably).
- Route/Ingress uses TLS (`edge` / `reencrypt` / `passthrough` as the standard says), with an explicit `haproxy.router.openshift.io/timeout` if requests take longer than the default.
- No `NodePort` or `LoadBalancer` services unless intended.

### Config hygiene
- Labels follow the standard (`app.kubernetes.io/name`, `instance`, `version`, `part-of`).
- ConfigMap changes trigger a rollout (checksum annotation in Helm, or a Kustomize `configMapGenerator`). Otherwise pods keep the old config.
- Namespaces are not hard-coded when the manifests are meant to be reused across environments.

## Do not
- Do not apply, delete, or patch anything on a live cluster as part of a review. Produce the corrected YAML and the dry-run result, and let the user apply it.
