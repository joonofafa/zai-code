# Symptom → cause → checks (read-only)

| Pod status / symptom | Usual causes | Checks |
|---|---|---|
| `Pending` | No node fits: CPU/memory/GPU requests, taints, node selector or affinity, quota, PVC not bound | `oc describe pod` Events (`0/N nodes are available: ...`), `oc describe quota`, `oc get pvc`, `oc get nodes -L <label>` |
| `ContainerCreating` for a long time | Volume mount or attach failure, image pull slow, CNI/IP exhaustion, missing ConfigMap or Secret | `oc describe pod` Events (`FailedMount`, `FailedCreatePodSandBox`), `oc get cm,secret <name>` |
| `ImagePullBackOff` / `ErrImagePull` | Wrong tag, registry not reachable from an air-gapped cluster, missing pull secret, mirror missing the image | Exact `image:` in the spec; `oc get secret -o name \| grep pull`; `oc get sa <sa> -o yaml` (imagePullSecrets); `oc get imagedigestmirrorset,imagecontentsourcepolicy` |
| `CrashLoopBackOff` | App exits on start: config error, missing env or secret, DB unreachable, permission denied (SCC/UID), wrong command | `oc logs <p> --previous`, exit code in `oc describe pod` (`Last State`): 1 = app error, 126/127 = command not executable / not found, 137 = SIGKILL (often OOM or a liveness kill), 139 = segfault, 143 = SIGTERM |
| `OOMKilled` | Memory limit too low or leak; JVM heap not sized to the container | `oc describe pod` (`Reason: OOMKilled`), limits vs usage `oc adm top pod <p> --containers`; JVM: check `-Xmx` vs limit (`MaxRAMPercentage`) |
| Restarts with no crash in logs | Liveness probe failing (timeout too short, slow start) | `oc describe pod` Events `Liveness probe failed`; compare `initialDelaySeconds`/`timeoutSeconds`; consider a `startupProbe` |
| Running but not Ready | Readiness probe failing, dependency down | Events `Readiness probe failed`, probe path/port vs the app; `oc get endpoints <svc>` (empty = not ready) |
| `CreateContainerConfigError` | Referenced ConfigMap/Secret/key missing | `oc describe pod` message names the key; `oc get cm/secret <name> -o jsonpath='{.data}'` keys only |
| `Init:Error` / `Init:CrashLoopBackOff` | Init container failing (migrations, waits) | `oc logs <p> -c <init>` |
| `Evicted` | Node pressure (disk, memory) | `oc describe pod` message, `oc describe node <n>` Conditions |
| `Terminating` stuck | Finalizers, a node that is gone, a volume detach hang | `oc get pod <p> -o jsonpath='{.metadata.finalizers}'`, node status. **Do not force-delete without approval** (`--grace-period=0 --force` can corrupt StatefulSet volumes) |
| Route 503 | No ready endpoints, selector mismatch, wrong targetPort | `oc get route <r> -o yaml` (to.name, port.targetPort), `oc get svc <s> -o yaml` (selector, ports), `oc get endpoints <s>`, pod labels `oc get pod --show-labels` |
| Route 502 / timeout | App slow or crashing under load, router timeout | App logs, the `haproxy.router.openshift.io/timeout` annotation, pod CPU throttling |
| Service unreachable inside the cluster | NetworkPolicy blocking, wrong port, DNS | `oc get networkpolicy`, `oc rsh <client-pod> curl -sv http://<svc>.<ns>.svc:<port>/` (read-only probe; ask before `rsh` into production), `oc rsh <pod> getent hosts <svc>` |
| PVC `Pending` | No StorageClass default, no matching PV, zone mismatch, quota | `oc describe pvc`, `oc get sc`, `oc get pv` |
| Permission denied writing files | Random UID cannot write image-owned dirs | Image `USER`/dir ownership; the dir must be group 0 writable (`chgrp -R 0 && chmod -R g=u` in the image build) or an emptyDir/PVC mounted there |
| HPA not scaling | No metrics, requests missing, max reached | `oc get hpa`, `oc describe hpa` (conditions), container `resources.requests` set |
| Job never finishes / keeps retrying | `backoffLimit`, app exit code | `oc describe job`, `oc logs job/<name>` |

## Useful read-only commands
```bash
oc get pods -o wide --sort-by=.status.startTime
oc get events --sort-by=.lastTimestamp --field-selector type=Warning | tail -40
oc get pod <p> -o jsonpath='{range .status.containerStatuses[*]}{.name}{"\t"}{.restartCount}{"\t"}{.lastState.terminated.reason}{"\t"}{.lastState.terminated.exitCode}{"\n"}{end}'
oc adm top pod --containers            # needs metrics
oc explain deployment.spec.template.spec.containers.resources   # field docs, offline from the API server
oc auth can-i <verb> <resource>        # what you are allowed to do
oc get all,cm,secret,pvc,route,networkpolicy -l app=<name>
oc rollout history deployment/<d>
oc get deployment <d> -o yaml > /tmp/<d>.yaml   # then Read the file (never page with less in a tool call)
```
`oc explain <resource>.<field> --recursive` is the offline documentation for every resource field, including CRDs. It comes from the cluster's own API schema.
