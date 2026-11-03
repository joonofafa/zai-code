# PL/SQL → PL/pgSQL

## Structure
| PL/SQL | PL/pgSQL |
|---|---|
| `CREATE OR REPLACE PROCEDURE p(a IN NUMBER, b OUT VARCHAR2) IS ... BEGIN ... END;` | `CREATE OR REPLACE PROCEDURE p(a numeric, INOUT b varchar DEFAULT NULL) LANGUAGE plpgsql AS $$ DECLARE ... BEGIN ... END; $$;` — `OUT` arguments on procedures need PG 14+. Before 14, use `INOUT` |
| Function `RETURN NUMBER IS` | `RETURNS numeric LANGUAGE plpgsql AS $$ ... $$` |
| Declarations between `IS` and `BEGIN` | `DECLARE` section |
| `PACKAGE` / `PACKAGE BODY` | a schema per package (`pkg.proc`); package variables → a session config (`set_config('pkg.var', v, false)` / `current_setting('pkg.var', true)`) or a temp table |
| Package initialization block | no equivalent; call an explicit init function |
| `%TYPE`, `%ROWTYPE` | supported |
| `RECORD` | `record`, or a composite type |
| `TABLE OF ... INDEX BY` (associative array) | arrays (`numeric[]`), `hstore`/`jsonb` for string keys, or a temp table |
| `VARRAY` / nested table | arrays |
| `BULK COLLECT INTO arr` | `SELECT array_agg(col) INTO arr FROM ...`, or process with a set-based statement |
| `FORALL i IN ... INSERT` | one set-based `INSERT ... SELECT unnest(arr)` |
| `EXECUTE IMMEDIATE sql USING a` | `EXECUTE sql USING a` (placeholders `$1`, `$2`; build identifiers with `format('%I', name)` and literals with `%L` — never by concatenating raw input) |
| `SELECT ... INTO v` with no rows | Oracle raises NO_DATA_FOUND. PG leaves v NULL unless you write `SELECT ... INTO STRICT v` |
| `SQL%ROWCOUNT` | `GET DIAGNOSTICS n = ROW_COUNT;` |
| `SQL%FOUND` / `SQL%NOTFOUND` | `FOUND` / `NOT FOUND` |
| Explicit cursor + `OPEN/FETCH/CLOSE`, `c%NOTFOUND` | same keywords; `EXIT WHEN NOT FOUND;` |
| `FOR r IN (SELECT ...) LOOP` | `FOR r IN SELECT ... LOOP` |
| `REF CURSOR` / `SYS_REFCURSOR` | `refcursor` |
| `DBMS_OUTPUT.PUT_LINE(x)` | `RAISE NOTICE '%', x;` |
| `RAISE_APPLICATION_ERROR(-20001, 'msg')` | `RAISE EXCEPTION 'msg' USING ERRCODE = 'P0001';` (map application codes to your own SQLSTATE or put them in the message) |
| `EXCEPTION WHEN NO_DATA_FOUND / TOO_MANY_ROWS / DUP_VAL_ON_INDEX / OTHERS` | `NO_DATA_FOUND`, `TOO_MANY_ROWS` (with STRICT), `unique_violation`, `OTHERS` |
| `SQLCODE` / `SQLERRM` | `SQLSTATE` / `SQLERRM` |
| `PRAGMA AUTONOMOUS_TRANSACTION` (for example, audit logs that survive rollback) | not supported — use `dblink` to a loopback connection if installed, or move the logging out of the transaction (application or queue). Flag every occurrence |
| `COMMIT` inside a procedure | allowed in PG procedures called with `CALL` outside an explicit transaction block; not allowed in functions |
| Triggers `BEFORE INSERT ... FOR EACH ROW` with `:NEW.col` | trigger function `RETURNS trigger` using `NEW.col`, then `CREATE TRIGGER ... EXECUTE FUNCTION f()`; return `NEW` (or `NULL` to skip) |
| `:OLD`, `INSERTING`/`UPDATING`/`DELETING` | `OLD`, `TG_OP = 'INSERT'` / `'UPDATE'` / `'DELETE'` |
| `DBMS_LOB`, `UTL_FILE`, `DBMS_SCHEDULER`, `DBMS_JOB`, `UTL_HTTP`, `UTL_MAIL` | no built-in equivalent — list them as "not converted" and agree on a replacement (application code, OS cron / pg_cron if installed) |

## Error-handling behavior
- An `EXCEPTION` block in PL/pgSQL creates a subtransaction, which costs time in tight loops. Use it at the statement level that needs it, not around every row.
- Once an error is not caught, the whole transaction is aborted. Oracle-style "log and continue" needs an explicit `BEGIN ... EXCEPTION ... END` around the statement that may fail.

## Testing converted code
- For each procedure, build a small fixture: input rows, call, expected output or table state. Run it in a transaction and roll back (`BEGIN; CALL ...; SELECT ...; ROLLBACK;`) on a test database.
- Cover the no-rows case, the many-rows case, NULL and empty-string inputs, and the error paths (duplicate key, invalid input).
