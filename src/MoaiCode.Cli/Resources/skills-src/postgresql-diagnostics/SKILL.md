---
name: postgresql-diagnostics
description: Use when a PostgreSQL (or PostgreSQL-based, e.g. eXperDB/EDB) database is slow, blocked, out of connections, lagging in replication, growing unexpectedly, or when a query plan needs to be explained. Read-only diagnosis first with catalog views; any change (kill, vacuum, index, config) only after the user confirms.
license: MATE Builder original — no third-party content
---

# PostgreSQL diagnostics (read-only first)

## Ground rules
- **Connect read-only when you can.** Use a monitoring role, or run `psql` with `-v ON_ERROR_STOP=1`, and start the session with `SET default_transaction_read_only = on;`.
- **Never run these without explicit user confirmation:** `pg_terminate_backend`, `pg_cancel_backend`, `VACUUM FULL`, `REINDEX`, `CLUSTER`, `ALTER SYSTEM`, `DROP`, `TRUNCATE`, `DELETE`/`UPDATE`, `CREATE INDEX` (unless `CONCURRENTLY` and agreed), or a config reload or restart. State the impact first. For example, `VACUUM FULL` takes an exclusive lock for the whole rewrite.
- **`EXPLAIN ANALYZE` really executes the statement.** For anything that writes, wrap it in `BEGIN; EXPLAIN (ANALYZE, BUFFERS) ...; ROLLBACK;`, and do it on a non-production copy unless told otherwise.
- **Check the server version first** (`SELECT version();`). Column names in the stats views change between versions; adapt the queries.
- Never print passwords or connection strings that contain them. Prefer `~/.pgpass` or `PGPASSFILE`.

## Triage order
1. **What is running right now** → `references/queries.md` §1 (active sessions, longest first).
2. **Who blocks whom** → §2 (lock tree via `pg_blocking_pids`).
3. **Connections** → §3 (counts by state and app vs `max_connections`; `idle in transaction` is the usual culprit).
4. **Slow statements over time** → §4 (`pg_stat_statements`, if the extension is installed).
5. **One slow query** → §5 (plan reading guide).
6. **Bloat, vacuum, and wraparound** → §6.
7. **Replication** → §7 (primary and standby views).
8. **Size and growth** → §8.
9. **Configuration actually in effect** → §9 (`pg_settings` with source).

## Reading a plan (short form)
- Compare **estimated rows vs actual rows** at each node. A gap of 10x or more usually means stale statistics (`ANALYZE`), correlated columns (extended statistics), or a function or cast that hides the column.
- `Seq Scan` on a big table with a selective filter → missing or unusable index. Check for implicit casts (`varchar = numeric`), functions on the column (`upper(col)`), leading-wildcard `LIKE '%x'`, and a type mismatch with the bind parameter.
- `Nested Loop` with a large outer row count → a bad estimate upstream; fix the estimate, not the join.
- `Sort` / `Hash` with `external merge` or `Batches > 1` → work_mem spill. Note this, but do not raise work_mem globally without the DBA.
- `Buffers: shared read` high vs `hit` → cold cache or I/O bound; compare with a second run.
- With `Rows Removed by Filter` large, the index is not selective for this predicate. Consider a composite or partial index.

## Common findings → safe next step
| Finding | Next step (propose, then wait for confirmation) |
|---|---|
| Long `idle in transaction` sessions holding locks | Identify the app and host from `pg_stat_activity`. Ask the owner to fix the code path. Terminate only with approval |
| Lock queue behind a DDL statement | The DDL waits for a long reader, and everyone queues behind it. Cancel the DDL (not the reader) with approval, and rerun with `lock_timeout` |
| Dead tuples high, autovacuum not keeping up | Check `pg_stat_user_tables.last_autovacuum` and the autovacuum settings per table; propose per-table tuning |
| `age(datfrozenxid)` approaching 2 billion | Urgent: tell the DBA immediately; a manual `VACUUM (FREEZE)` on the oldest tables is needed |
| Replication lag growing | Check whether it is the network (sent vs write lsn), standby I/O (write vs replay), or long queries on the standby (`hot_standby_feedback`, conflicts) |
| Disk filling with WAL | Look for inactive replication slots (`pg_replication_slots.active = false`) holding WAL, and failing `archive_command` (`pg_stat_archiver`) |

## Report
Report the evidence (query output, trimmed), the diagnosis, the proposed action with its lock and impact, and what you did not check.
