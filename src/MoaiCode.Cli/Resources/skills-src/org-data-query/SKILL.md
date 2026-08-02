---
name: org-data-query
description: Use when querying the organization data warehouse (the 데이터함 / OrgDatas Record DB) with SQL — especially with Korean column names, or when a query fails with "query must reference an accessible table". Ensures the correct, first-try read-only SELECT workflow.
---

# Organization data query (OrgDatas) — correct SELECT workflow

Follow this every time you query the org data warehouse. It prevents the most common failure (a rejected first query) and wasted round-trips.

## Rules

1. **List the schema FIRST.** Always call `OrgDatasList` before `OrgDatas` to get the exact table names, columns, types, and sample values. Never guess a table or column name.

2. **Use the exact `table_name` from `OrgDatasList`** in your `FROM` clause — copy it verbatim.

3. **Do NOT wrap identifiers in double quotes.** The server rejects double-quoted identifiers (Korean column/table names included) with `400: query must reference an accessible table`. Write bare identifiers exactly as `OrgDatasList` returned them:
   - ✅ `SELECT 연월, 브랜드명, SUM(총이용금액) AS 매출 FROM cache_dessert_month_brand GROUP BY 연월, 브랜드명`
   - ❌ `SELECT "연월", "브랜드명" FROM "cache_dessert_month_brand"`  ← rejected

4. **Read-only only.** Only `SELECT / WITH / EXPLAIN / SHOW / DESCRIBE / PRAGMA` are allowed. No `INSERT/UPDATE/DELETE/DDL`, no multiple statements.

5. **Bound exploration.** Add `LIMIT` when sampling. Aggregate with `GROUP BY` and give columns readable aliases.

## If a query returns 400 / an error, re-check in this order
1. Did you call `OrgDatasList` first and use the exact table name?
2. Are any identifiers double-quoted? Remove the quotes.
3. Is it strictly one read-only `SELECT`?

## After you have the data
- For a spreadsheet/chart, follow up with `XlsxCreate` (native — no Excel needed).
- For a report or slides, use `DocxCreate` / `PptxCreate`.
- Do NOT install packages or write scripts to build Office files — the native tools already do it.
