---
name: business-report
description: Use when producing a business report or summary document (Word or Excel) from data or organization documents — monthly performance, analysis summaries, KPI reports, and similar.
---

# Business report authoring (Word / Excel)

Produce a clear, executive-ready report using the built-in Office tools. Never install packages (python-docx, openpyxl, etc.) or write scripts — `DocxCreate` / `XlsxCreate` already build valid files with no Office install.

## 1. Gather grounded data first
- Numbers: query the data warehouse via `OrgDatas` (follow the org-data-query workflow — list schema first, no double-quoted identifiers).
- Context: pull relevant org documents via `OrgDocs`.
- Note the source and period for every figure; do not invent numbers.

## 2. Structure (lead with the conclusion)
1. **제목 · 대상 기간** (title and reporting period)
2. **핵심 요약 (Executive summary)** — 3–5 bullets with the key takeaways and the bottom line, up front.
3. **주요 지표 (Key metrics)** — a table of the core figures.
4. **분석 · 인사이트** — what changed, why it matters, notable movements.
5. **결론 · 제언** — conclusion and concrete recommendations / next steps.

## 3. Build it
- **Narrative report → `DocxCreate`**: use headings, bullet lists, and tables. Keep prose high-signal.
- **Figures / dashboards → `XlsxCreate`**: put tabular data in sheets and add a chart (bar/line/pie). Cell values auto-type (formulas, %, thousands separators) — rely on that instead of pre-formatting strings.
- Formatting: consistent units, thousands separators, clear period labels, and a total row where it helps.

## 4. Verify
- Re-open the produced file with `OfficeDocInspect` to confirm it is valid and renders (no corruption).
- Then present the file to the user as the deliverable.
