# Oracle → PostgreSQL: SQL, types, functions

Check the target server version with `SELECT version();`. Notes marked "PG 15+" or "PG 16+" need that version or later.

## Types
| Oracle | PostgreSQL | Note |
|---|---|---|
| `VARCHAR2(n)` / `NVARCHAR2(n)` | `varchar(n)` | Oracle n may be BYTE semantics; PG n is characters. Korean UTF-8 text that fit in `VARCHAR2(30 BYTE)` fits in `varchar(30)` |
| `CHAR(n)` | `char(n)` (prefer `varchar(n)`) | |
| `NUMBER(p,0)`, p ≤ 4 / ≤ 9 / ≤ 18 | `smallint` / `integer` / `bigint` | Only after you confirm the range |
| `NUMBER(p,s)` | `numeric(p,s)` | |
| `NUMBER` (no precision) | `numeric` | Exact; check performance on hot columns |
| `BINARY_FLOAT` / `BINARY_DOUBLE` | `real` / `double precision` | |
| `DATE` | `timestamp(0)` | Oracle DATE includes the time of day |
| `TIMESTAMP(n)` | `timestamp(n)` | |
| `TIMESTAMP WITH TIME ZONE` | `timestamptz` | PG stores UTC and does not keep the original zone |
| `INTERVAL DAY TO SECOND` | `interval` | |
| `CLOB` / `NCLOB` | `text` | |
| `BLOB` / `RAW(n)` / `LONG RAW` | `bytea` | |
| `XMLTYPE` | `xml` | |
| `ROWID` | none | Use the primary key. `ctid` is not stable |
| `BOOLEAN` (23ai) / `NUMBER(1)` flag | `boolean` or `smallint` | |

## Functions and expressions
| Oracle | PostgreSQL |
|---|---|
| `NVL(a,b)` | `COALESCE(a,b)` |
| `NVL2(a,b,c)` | `CASE WHEN a IS NOT NULL THEN b ELSE c END` |
| `DECODE(x,a,r1,b,r2,def)` | `CASE x WHEN a THEN r1 WHEN b THEN r2 ELSE def END` — DECODE treats NULL = NULL as a match; for NULL search values write `CASE WHEN x IS NULL THEN ...` |
| `SYSDATE` | `LOCALTIMESTAMP(0)` (transaction start) / `clock_timestamp()` (real time) |
| `SYSTIMESTAMP` | `current_timestamp` / `clock_timestamp()` |
| `TRUNC(date)` | `date_trunc('day', ts)` |
| `TRUNC(date,'MM')` | `date_trunc('month', ts)` |
| `TRUNC(n, d)` (number) | `trunc(n, d)` |
| `ADD_MONTHS(d,n)` | `d + make_interval(months => n)` — check month-end: Oracle maps the last day to the last day, PG keeps the day number |
| `MONTHS_BETWEEN(a,b)` | no direct equivalent; write a function that matches the Oracle rounding you need |
| `LAST_DAY(d)` | `(date_trunc('month', d) + interval '1 month - 1 day')::date` |
| `TO_CHAR(d,'YYYYMMDDHH24MISS')` | `to_char(d,'YYYYMMDDHH24MISS')` — most masks match; check `FM`, `D`, `DY`, `RR` |
| `TO_DATE(s,'YYYYMMDD')` | `to_timestamp(s,'YYYYMMDD')::timestamp(0)` (or `to_date` if no time) — PG is less strict about bad input; validate |
| `TO_NUMBER(s)` | `s::numeric` (or `to_number(s, mask)`) |
| `SUBSTR(s,p,n)` | `substr(s,p,n)` — Oracle `p=0` behaves like 1; negative `p` counts from the end in Oracle but not in PG: use `right()` |
| `INSTR(s,t)` | `strpos(s,t)`; with start or occurrence arguments, write a helper |
| `LENGTH` / `LENGTHB` | `length` / `octet_length` |
| `LPAD`, `RPAD`, `TRIM`, `UPPER`, `LOWER`, `REPLACE` | same names |
| `REGEXP_LIKE(s,p)` | `s ~ p` (`~*` for case-insensitive); `regexp_like` exists in PG 15+ |
| `REGEXP_SUBSTR(s,p)` | `substring(s from p)` or `(regexp_match(s,p))[1]`; `regexp_substr` exists in PG 15+ |
| `REGEXP_REPLACE(s,p,r)` | `regexp_replace(s,p,r,'g')` — Oracle replaces all matches by default, PG only the first without `'g'` |
| `LISTAGG(x, ',') WITHIN GROUP (ORDER BY y)` | `string_agg(x, ',' ORDER BY y)` |
| `WM_CONCAT` | `string_agg` |
| `GREATEST` / `LEAST` | same names — Oracle returns NULL if any argument is NULL, PG ignores NULLs |
| `MOD(a,b)` | `mod(a,b)` or `a % b` |
| `SIGN`, `ROUND`, `CEIL`, `FLOOR`, `ABS`, `POWER` | same names |
| `SYS_GUID()` | `gen_random_uuid()` (PG 13+) |
| `USER` | `current_user` |
| `SYS_CONTEXT('USERENV','...')` | `current_setting(...)`, `inet_client_addr()`, `current_database()` |
| `x || NULL` | Oracle keeps `x`; PG gives NULL — use `concat(x, y)` or `COALESCE` |

## Query constructs
| Oracle | PostgreSQL |
|---|---|
| `SELECT 1 FROM DUAL` | `SELECT 1` |
| `SELECT * FROM (SELECT ... ORDER BY x) WHERE ROWNUM <= 10` | `SELECT ... ORDER BY x LIMIT 10` |
| `WHERE ROWNUM <= 10` (no ORDER BY in the inner query) | `LIMIT 10` — "any 10 rows"; flag it as nondeterministic |
| Paging with `ROWNUM` between n and m | `ORDER BY ... LIMIT (m-n+1) OFFSET (n-1)`; for deep pages use keyset paging (`WHERE key > :last ORDER BY key LIMIT n`) |
| `OFFSET n ROWS FETCH NEXT m ROWS ONLY` | same syntax |
| `a.id = b.id(+)` | `a LEFT JOIN b ON a.id = b.id` (put b's filters in ON) |
| `CONNECT BY PRIOR id = parent_id START WITH parent_id IS NULL` | `WITH RECURSIVE t AS (SELECT ..., 1 AS level FROM x WHERE parent_id IS NULL UNION ALL SELECT x..., t.level+1 FROM x JOIN t ON x.parent_id = t.id) SELECT ...` |
| `LEVEL`, `SYS_CONNECT_BY_PATH`, `CONNECT_BY_ISLEAF` | carry a level column, a path column (`t.path || '/' || x.name`), and a leaf test (`NOT EXISTS` child) in the CTE |
| `ORDER SIBLINGS BY` | build a sort path array in the CTE and `ORDER BY path` |
| `NOCYCLE` | track visited ids in an array (`CYCLE` clause in PG 14+) |
| `MERGE INTO t USING s ON (...) WHEN MATCHED ... WHEN NOT MATCHED ...` | PG 15+: `MERGE` (similar syntax); older: `INSERT ... ON CONFLICT (key) DO UPDATE SET ...` (needs a unique index on the key) |
| `MINUS` | `EXCEPT` |
| `INSERT ALL ... INTO ... INTO ...` | separate INSERTs, or a CTE with `INSERT ... RETURNING` |
| `SELECT ... FOR UPDATE NOWAIT / SKIP LOCKED / WAIT n` | `NOWAIT`, `SKIP LOCKED` same; `WAIT n` → `SET lock_timeout = 'ns'` |
| `seq.NEXTVAL` / `seq.CURRVAL` | `nextval('seq')` / `currval('seq')` |
| Column default `seq.NEXTVAL` / identity | `bigint GENERATED BY DEFAULT AS IDENTITY` or `DEFAULT nextval('seq')` |
| `CREATE SYNONYM s FOR owner.t` | `search_path`, or a view; there are no synonyms |
| Optimizer hints `/*+ INDEX(...) */` | ignored — remove, and fix with indexes and statistics |
| `GLOBAL TEMPORARY TABLE` | `CREATE TEMP TABLE` per session (definition is not shared), or a regular unlogged table plus a session key |
| Partitioning `PARTITION BY RANGE` | declarative partitioning (`PARTITION BY RANGE`); interval partitions must be created ahead (cron or pg_partman if installed) |
| `ANALYZE TABLE ... COMPUTE STATISTICS` / DBMS_STATS | `ANALYZE t;` |
| Database links `@dblink` | `postgres_fdw` / `oracle_fdw` if installed — confirm with the DBA |

## DDL notes
- Unquoted identifiers: keep them unquoted, and PG folds them to lowercase.
- `CREATE OR REPLACE VIEW` cannot drop or reorder columns in PG. Use `DROP VIEW` + `CREATE`, and check dependents first.
- Indexes on expressions (`UPPER(name)`) work the same way. Function-based indexes need `IMMUTABLE` functions.
- Constraints: `DEFERRABLE` is supported. `ENABLE NOVALIDATE` → `NOT VALID` + `VALIDATE CONSTRAINT` later.
- Comments: `COMMENT ON TABLE/COLUMN` is the same.
