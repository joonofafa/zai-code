---
name: openshift-diagnostics
description: Use when a workload on OpenShift or Kubernetes is failing, not starting, restarting, unreachable, or slow — pods in CrashLoopBackOff/ImagePullBackOff/Pending, routes or services not answering, SCC/permission errors, PVCs not binding, GPU pods not scheduling. Read-only investigation with oc/kubectl first; changes only after the user confirms.
license: MATE Builder original — no third-party content
---

# OpenShift / Kubernetes diagnostics

Use `oc` when it is present; it accepts every `kubectl` subcommand and adds OpenShift ones (`oc get route`, `oc adm`, `oc debug`). With plain Kubernetes, replace `oc` with `kubectl`.

## Ground rules
- **Confirm the target first:** `oc whoami`, `oc whoami --show-server`, and `oc project -q` (or `kubectl config current-context`). State the cluster and namespace in your answer. Never assume production or non-production.
- **Read-only by default.** `get`, `describe`, `logs`, `events`, `explain`, `auth can-i`, and `adm top` are safe.
- **Ask before any change, and say what it affects:** `delete`, `apply`, `create`, `replace`, `patch`, `edit`, `scale`, `rollout restart/undo`, `set`, `label/annotate` on live objects, `cordon/drain/uncordon`, `adm` policy or node commands, `exec` into production pods, and `debug node/...`.
- **Prefer a dry run for proposed changes:** `oc apply --dry-run=server -f x.yaml` and `oc diff -f x.yaml`.
- **Never print secrets.** `oc get secret x -o yaml` exposes base64 values. List the keys only: `oc get secret x -o jsonpath='{.data}' | jq 'keys'`, or `oc describe secret x`.
- **Logs can contain personal or card data.** Fetch narrow windows (`--since=10m`, `--tail=200`) and summarize; do not paste raw customer data.

## Triage flow
1. **Overview:** `oc get pods -o wide`, then `oc get events --sort-by=.lastTimestamp | tail -30`.
2. **The failing pod:** `oc describe pod <p>`. Read Conditions, the container `State`/`Last State` (exit code, reason), and Events at the bottom.
3. **Logs:** `oc logs <p> -c <container> --tail=200`. For the crashed instance, use `--previous`. For init containers, `-c <init-name>`.
4. **Owner chain:** `oc get pod <p> -o jsonpath='{.metadata.ownerReferences}'`, then the Deployment/StatefulSet/Job, `oc rollout status`, and `oc rollout history`.
5. **Match the symptom** in `references/oc-commands.md` (status → cause → checks).
6. **Report:** evidence lines, root cause, a proposed fix as a diff or YAML, its dry-run result, and the rollback.

## OpenShift-specific traps
- **Security Context Constraints (SCC).** By default pods run as a random high UID with `restricted-v2`. Images that need root, a fixed UID, port < 1024, or `chown` at startup fail with `permission denied` or CrashLoopBackOff. Check `oc get pod <p> -o yaml | grep -A3 'openshift.io/scc'` and the image's `USER`. The fix is usually in the image (group-0 writable dirs, non-root port), not a privileged SCC. Granting `anyuid`/`privileged` is a security decision for the platform team.
- **Routes vs Ingress.** External access is a `Route` (`oc get route`). 503 "Application is not available" means the router has no ready endpoints: check `oc get endpoints <svc>`, service selector vs pod labels, `targetPort`, and readiness probes.
- **Internal registry and pull secrets.** `ImagePullBackOff` on an air-gapped cluster usually means the image reference points outside (docker.io, quay.io) or the mirror (`ImageContentSourcePolicy` / `ImageDigestMirrorSet`) lacks the tag. Check the exact image string and `oc get secret` for the pull secret linked to the service account.
- **Projects have quotas and limit ranges.** `oc describe quota` and `oc describe limitrange`. Pending pods or `exceeded quota` events come from here.
- **Builds/ImageStreams.** `oc get bc,builds,is` and `oc logs build/<name>` for BuildConfig failures.
- **Node-level checks** need cluster-admin (`oc adm top nodes`, `oc debug node/<n>`). If you lack rights (`oc auth can-i ...`), say so and hand over the exact commands for the platform team.

## GPU workloads (OpenShift AI / NVIDIA GPU Operator)
- Pending with `Insufficient nvidia.com/gpu`: compare `oc describe node <n> | grep -A5 Allocatable` with the requests; check taints/tolerations and node selectors (`nvidia.com/gpu.present=true`).
- Operator health: `oc get pods -n nvidia-gpu-operator` (namespace may differ), `oc get clusterpolicy`.
- Model-serving pods (KServe `InferenceService`, `ServingRuntime`): `oc get inferenceservice`, `oc describe inferenceservice <n>`, then the predictor pod logs. For vLLM sizing and flags, see the `vllm-serving` skill.
