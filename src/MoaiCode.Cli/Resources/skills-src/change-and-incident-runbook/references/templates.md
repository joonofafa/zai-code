# Templates

Write in the team's language (Korean if the user writes in Korean). Use absolute timestamps with the timezone (for example 2026-11-03 14:05 KST).

## A. Change plan
```markdown
# Change plan: <short title>

- Change ID / ticket: <id or "none">
- Requester / approver: <names or roles>
- Environment: <prod|stage|dev> — <cluster/namespace, hosts, DB>  (confirmed by: <command and output>)
- Window: <start–end>; freeze check: <ok / n.a.>
- Summary: <what changes and why, 2–3 lines>
- Blast radius: <services and users affected; expected downtime or degradation>

## Pre-checks (state before the change)
| # | Check | Command | Result |
|---|---|---|---|
| P1 | Current version | `...` | `...` |
| P2 | Backup of config / manifest / schema | `...` | saved to `<path>` (sha256 `<...>`) |
| P3 | Health and baseline metrics | `...` | `...` |

## Steps
| # | Action | Command | Expected | Stop if | Rollback |
|---|---|---|---|---|---|
| 1 | ... | `...` | ... | ... | `...` |
| 2 | ... | `...` | ... | ... | `...` |

## Verification
| # | Check | Command | Pass criterion |
|---|---|---|---|
| V1 | Service health | `...` | ... |
| V2 | Error rate vs baseline | `...` | ≤ baseline |
| V3 | Business smoke test | <who / how> | ... |

## Rollback plan (full)
<ordered steps to return to the pre-check state; how long it takes; data implications>

## Execution log
| Time | Step | Result | Note |
|---|---|---|---|
```

## B. Incident report
```markdown
# Incident report: <short title>

- Severity: <level per team definition>
- Status: <investigating | mitigated | resolved>
- Period: <start> – <end> (<duration>)
- Detected by: <alert / user report / monitoring>, at <time>
- Impact: <who/what was affected, how many requests, users or transactions, approximately; data loss yes/no>

## Timeline
| Time | Event | Source |
|---|---|---|
| | first error | <log or metric> |
| | detection | |
| | mitigation applied | |
| | recovery confirmed | |

## Root cause
<what failed and why; evidence. If not yet known: "Unknown — checking X, Y">

## Trigger
<the change or event that set it off, if different from the root cause>

## Mitigation and recovery
<what was done, by whom, when; what made it recover>

## What went well / what was hard
- ...

## Follow-up actions
| Action | Owner | Due | Ticket |
|---|---|---|---|
```

## C. Quick evidence checklist (incident first 10 minutes)
- [ ] Exact start time from metrics or logs
- [ ] Last deploys and config changes around that time
- [ ] Error messages (top 3 by count), trimmed and masked
- [ ] Resource saturation: CPU, memory, disk, connections, threads
- [ ] Dependencies: DB, cache, message broker, external interfaces
- [ ] Scope: all nodes or pods, or a subset; all users or a subset
