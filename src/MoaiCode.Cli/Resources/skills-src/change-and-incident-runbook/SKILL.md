---
name: change-and-incident-runbook
description: Use when preparing a production change (deploy, config change, DB migration, patch, restart, scaling, network/firewall rule) or when responding to an incident on internal infrastructure — produces the change plan with pre-checks, execution steps, verification and rollback, or runs a structured incident investigation and writes the incident report.
license: MATE Builder original — no third-party content
---

# Change and incident runbooks

Two modes. Pick the one that matches the request. The templates are in `references/templates.md`. Fill them with real values from the environment, never with placeholders presented as facts.

## A. Planned change
1. **Understand the change.** Identify what changes, where (hosts, cluster or namespace, DB), why, and who approves. Confirm the target environment from evidence (`hostname`, `oc whoami --show-server`, `SELECT current_database(), inet_server_addr();`) and state it.
2. **Pre-checks (read-only).** Capture the current state so you can compare and roll back: versions, config files (copy them to a dated backup path, read-only), replica counts, `oc get deploy -o yaml` saved to a file, DB row counts or schema dump for touched tables, and service health and key metrics. Check that the change window and the freeze calendar allow it, if the team uses one.
3. **Write the plan** (template A) **before** anything is executed:
   - numbered steps, each with the exact command, the expected output, and a **stop condition**
   - the verification after each risky step
   - a rollback for every step, tested or at least dry-run where possible (`--dry-run=server`, `terraform plan`, `ansible --check --diff`, `BEGIN ... ROLLBACK`)
   - blast radius and expected user impact
4. **Get explicit approval** from the user for the plan. Then execute **one step at a time**: show the command, run it, show the result, check the stop condition, and continue only if it is met. If anything unexpected appears, stop and report; do not improvise fixes in production.
5. **Post-checks.** Health, metrics, logs (errors per minute compared with the pre-check baseline), and a business smoke test agreed with the owner.
6. **Record.** Fill in what was actually done, the timings, deviations from the plan, and the final state.

## B. Incident response
1. **Stabilize first, diagnose second.** Ask: is there a known safe mitigation (roll back the last change, fail over, scale up, disable a feature flag)? Propose it with its risk, and let the on-call owner decide.
2. **Establish the timeline.** When did it start (metrics, first error log line)? What changed around then (deploys, `oc rollout history`, config commits, cron jobs, certificate expiry, the batch schedule)?
3. **Scope.** Which services, users, and regions are affected? Is it all requests or a fraction? Every node, or one?
4. **Collect the evidence (read-only).** Logs for narrow windows, events, resource usage (`top`, `df -h`, `free -m`, `ss -s`, `oc adm top`), DB sessions and locks (see the `postgresql-diagnostics` skill), and pod states (see `openshift-diagnostics`).
5. **Hypotheses.** List the candidates with the evidence for and against each. Test the cheapest disconfirming check first. Do not stack fixes: change one thing, observe, then decide.
6. **Write the report** (template B). Keep it factual and blameless: timeline in absolute timestamps with the timezone, impact, root cause (or "unknown" with what is still being checked), detection, mitigation, and follow-up actions with owners.

## Rules for both modes
- Every command that changes state needs the user's confirmation, even if a previous similar command was approved.
- Keep a running log of the commands you executed and their outputs (trimmed) in the conversation. That log is the audit trail.
- Mask personal and card data in any log excerpt you quote.
- Prefer reversible actions (scale, roll back, feature flag) to irreversible ones (delete, drop, force-push, data fixes). Irreversible actions need a backup that you have verified.
