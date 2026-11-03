# PostgreSQL diagnostic queries (read-only)

Written for PostgreSQL 13–17. Where a column differs by version, the note says so. Run them with `psql -X -A -F $'\t' -c "..."` to get compact output, or `\x auto` for wide rows.

## 1. Active sessions, longest first
```sql
SELECT pid, usename, application_name, client_addr, state,
       now() - xact_start  AS xact_age,
       now() - query_start AS query_age,
       wait_event_type, wait_event,
       left(query, 200) AS query
FROM pg_stat_activity
WHERE state <> 'idle' AND pid <> pg_backend_pid()
ORDER BY xact_start NULLS LAST
LIMIT 50;
```

## 2. Blocking tree
```sql
SELECT a.pid, pg_blocking_pids(a.pid) AS blocked_by,
       a.usename, a.state, now() - a.query_start AS waiting,
       a.wait_event_type, a.wait_event, left(a.query, 150) AS query
FROM pg_stat_activity a
WHERE cardinality(pg_blocking_pids(a.pid)) > 0
ORDER BY waiting DESC;
```
To see what a blocker holds:
```sql
SELECT l.locktype, l.mode, l.granted, l.relation::regclass, l.transactionid
FROM pg_locks l WHERE l.pid = <blocker_pid>;
```

## 3. Connections
```sql
SELECT state, application_name, usename, count(*)
FROM pg_stat_activity GROUP BY 1,2,3 ORDER BY 4 DESC;

SELECT setting::int AS max_connections,
       (SELECT count(*) FROM pg_stat_activity) AS current
FROM pg_settings WHERE name = 'max_connections';

-- idle in transaction for more than 5 minutes
SELECT pid, usename, application_name, client_addr, now() - state_change AS idle_for, left(query,150)
FROM pg_stat_activity
WHERE state = 'idle in transaction' AND now() - state_change > interval '5 minutes';
```

## 4. Top statements (needs pg_stat_statements)
Check first: `SELECT extname FROM pg_extension WHERE extname = 'pg_stat_statements';`
```sql
-- PG 13+ column names (total_exec_time, mean_exec_time)
SELECT queryid, calls,
       round(total_exec_time::numeric, 0) AS total_ms,
       round(mean_exec_time::numeric, 2)  AS mean_ms,
       rows, shared_blks_read, shared_blks_hit,
       left(query, 200) AS query
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 20;
```
(PG 12 and older: `total_time`, `mean_time`.)

## 5. One query's plan
```sql
EXPLAIN (ANALYZE, BUFFERS, VERBOSE, SETTINGS) <query>;   -- SETTINGS needs PG 12+
-- writes: BEGIN; EXPLAIN (ANALYZE, BUFFERS) <dml>; ROLLBACK;
```
Statistics freshness for the tables involved:
```sql
SELECT relname, n_live_tup, n_dead_tup, last_analyze, last_autoanalyze, last_vacuum, last_autovacuum
FROM pg_stat_user_tables WHERE relname IN ('<t1>','<t2>');
```
Indexes on a table, with usage:
```sql
SELECT i.indexrelid::regclass AS index, s.idx_scan, pg_size_pretty(pg_relation_size(i.indexrelid)) AS size,
       pg_get_indexdef(i.indexrelid) AS def
FROM pg_index i JOIN pg_stat_user_indexes s ON s.indexrelid = i.indexrelid
WHERE i.indrelid = '<schema.table>'::regclass;
```

## 6. Vacuum, bloat, wraparound
```sql
-- dead tuples
SELECT relname, n_live_tup, n_dead_tup,
       round(100.0 * n_dead_tup / nullif(n_live_tup + n_dead_tup, 0), 1) AS dead_pct,
       last_autovacuum
FROM pg_stat_user_tables ORDER BY n_dead_tup DESC LIMIT 20;

-- transaction ID age (warning well before 2,000,000,000)
SELECT datname, age(datfrozenxid) AS xid_age FROM pg_database ORDER BY 2 DESC;
SELECT c.oid::regclass, age(c.relfrozenxid) AS xid_age
FROM pg_class c WHERE c.relkind IN ('r','m','t') ORDER BY 2 DESC LIMIT 20;

-- vacuum running now
SELECT * FROM pg_stat_progress_vacuum;
```
Things that hold back cleanup: long transactions (§1), `idle in transaction` sessions (§3), abandoned prepared transactions (`SELECT * FROM pg_prepared_xacts;`), inactive replication slots (§7), and `hot_standby_feedback` from a standby with long queries.

## 7. Replication
On the primary:
```sql
SELECT application_name, client_addr, state, sync_state,
       pg_wal_lsn_diff(pg_current_wal_lsn(), sent_lsn)   AS send_lag_bytes,
       pg_wal_lsn_diff(sent_lsn, write_lsn)              AS write_lag_bytes,
       pg_wal_lsn_diff(write_lsn, replay_lsn)            AS replay_lag_bytes,
       write_lag, flush_lag, replay_lag
FROM pg_stat_replication;

SELECT slot_name, slot_type, active,
       pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) AS retained_wal
FROM pg_replication_slots;

SELECT * FROM pg_stat_archiver;   -- failed_count, last_failed_wal
```
On a standby:
```sql
SELECT pg_is_in_recovery(), pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn(),
       now() - pg_last_xact_replay_timestamp() AS replay_delay;
SELECT * FROM pg_stat_wal_receiver;
SELECT * FROM pg_stat_database_conflicts WHERE datname = current_database();
```

## 8. Size and growth
```sql
SELECT datname, pg_size_pretty(pg_database_size(datname)) FROM pg_database ORDER BY pg_database_size(datname) DESC;

SELECT n.nspname || '.' || c.relname AS relation,
       pg_size_pretty(pg_total_relation_size(c.oid)) AS total,
       pg_size_pretty(pg_relation_size(c.oid))       AS heap,
       pg_size_pretty(pg_indexes_size(c.oid))        AS indexes
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r','p','m') AND n.nspname NOT IN ('pg_catalog','information_schema')
ORDER BY pg_total_relation_size(c.oid) DESC LIMIT 20;
```
WAL directory size from SQL (PG 10+, needs the superuser or `pg_monitor` role): `SELECT pg_size_pretty(sum(size)) FROM pg_ls_waldir();`

## 9. Settings in effect
```sql
SELECT name, setting, unit, source, sourcefile, pending_restart
FROM pg_settings
WHERE name IN ('max_connections','shared_buffers','work_mem','maintenance_work_mem','effective_cache_size',
               'max_wal_size','checkpoint_timeout','autovacuum','autovacuum_vacuum_scale_factor',
               'idle_in_transaction_session_timeout','statement_timeout','lock_timeout',
               'hot_standby_feedback','synchronous_commit','wal_level','max_replication_slots')
ORDER BY name;
```
`source = 'configuration file'` together with `sourcefile` tells you which file to look at, but changing it is the DBA's call.
