---
name: oracle-to-postgresql
description: Use when converting Oracle SQL, DDL, PL/SQL, or application queries to PostgreSQL (including PostgreSQL-based products such as eXperDB, EDB, or Tibero-to-PG work), or when reviewing a migrated query that returns different results. Covers function and syntax mapping, NULL and empty-string semantics, types, sequences, paging, hierarchical queries, and PL/SQL to PL/pgSQL.
license: MATE Builder original — no third-party content
---

# Oracle → PostgreSQL conversion

Most migration bugs are not syntax errors. They are queries that still run but return different rows. Convert for **identical results first** and performance second, and prove equivalence on real data.

## Procedure
1. **Inventory.** Collect the objects in scope: tables, views, sequences, synonyms, packages, procedures, functions, triggers, and the SQL embedded in the application (MyBatis XML, JPA native queries, string-built SQL). Grep the code for Oracle markers: `ROWNUM`, `NVL`, `DECODE`, `SYSDATE`, `(+)`, `CONNECT BY`, `MERGE INTO`, `DUAL`, `TO_CHAR(`, `TO_DATE(`, `.NEXTVAL`, `ROWID`, `/*+`, `PRAGMA`, `%TYPE`, `BULK COLLECT`.
2. **Map** each construct with `references/sql-mapping.md` (SQL, types, functions) and `references/plsql-mapping.md` (procedural code). Read the reference before converting; do not convert from memory.
3. **Flag semantic traps** in every converted statement. The checklist below is the minimum.
4. **Prove equivalence.** Run the old and new queries on the same data. Compare row counts, then the full sorted result (for example, `EXCEPT` both ways on a staging copy, or checksums of the sorted output). Include NULL, empty-string, boundary-date, and multibyte (Korean) test rows.
5. **Check plans.** `EXPLAIN (ANALYZE, BUFFERS)` the converted hot queries on a non-production copy. Oracle hints do nothing in PostgreSQL; fix plans with indexes, statistics (`ANALYZE`), or rewrites, never with hint comments.
6. **Report** each object as converted / converted-with-behavior-change / not converted, listing every behavior change explicitly.

## Semantic traps (check every statement)
- **Empty string is NULL in Oracle, not in PostgreSQL.** `WHERE col = ''`, `NVL(col,'')`, `col IS NULL` on text columns, concatenation (`'a' || NULL` is `'a'` in Oracle and NULL in PG), and `LENGTH('')` (NULL in Oracle, 0 in PG) all change. Decide per column: normalize data (`NULLIF(col,'')`), or use `COALESCE`/`concat()` (which ignores NULLs).
- **ROWNUM is applied before ORDER BY** in the same query block. `WHERE ROWNUM <= 10 ORDER BY x` in Oracle returns 10 arbitrary rows, sorted. The faithful conversion is not `ORDER BY x LIMIT 10`. Confirm what the business intended.
- **DATE in Oracle carries time.** Map to `timestamp(0)`, not `date`, unless the column truly holds no time. `SYSDATE` → `LOCALTIMESTAMP(0)` (or `clock_timestamp()` if per-row time inside a transaction matters). `now()` is fixed for the whole transaction.
- **Date arithmetic.** `d + 1` (days) → `d + interval '1 day'`; `d1 - d2` returns a number of days in Oracle and an `interval` in PG (use `extract(epoch from d1 - d2)/86400`).
- **Implicit conversion.** Oracle silently converts `'123' = 123`; PostgreSQL raises an error or picks a different operator. Add explicit casts, and match bind-variable types in the application (JDBC `setString` against a numeric column).
- **NUMBER without precision** → `numeric` (exact, slower). Use `integer`/`bigint` only after you confirm the value range and that no fractions occur. `NUMBER(1)` used as a flag → `smallint` or `boolean` only if every reader is updated.
- **Sort order and collation.** PG text ordering depends on the database collation. Korean or mixed-case sorts can differ from Oracle `NLS_SORT`. Use `COLLATE "C"` for byte order where results must match.
- **Case of identifiers.** Unquoted Oracle names are uppercase; unquoted PG names fold to lowercase. Never quote names in converted DDL unless the application quotes them.
- **Outer join `(+)`** → explicit `LEFT JOIN`. Move filter predicates on the optional table into the `ON` clause, or the join silently becomes an inner join.
- **Integer division.** `5/2` is 2.5 in Oracle and 2 in PG for integers. Cast one side to `numeric`.
- **Transactions.** In PG, one error aborts the whole transaction until rollback, and DDL is transactional. Procedures that "continue after an error" need `EXCEPTION` blocks or savepoints.
- **Sequences.** `seq.NEXTVAL` → `nextval('seq')`. Gaps and caching differ. `CURRVAL` needs a prior `nextval` in the same session.
- **Empty LOBs, `CHAR` padding, trailing spaces.** `CHAR(n)` comparisons ignore trailing spaces in both, but `VARCHAR2` vs `varchar` concatenation and `LIKE` patterns on `CHAR` columns can differ. Prefer `varchar`/`text`.

## Safety
- Convert and test against a copy. Never run converted DDL or `DELETE`/`UPDATE` against production without the user's explicit go-ahead and a rollback plan.
- Keep the original Oracle text next to each converted statement (as comments, or in a mapping table) so a reviewer can compare them.
- Do not change application semantics to "fix" an Oracle quirk silently. Flag it, and let the owner decide.
