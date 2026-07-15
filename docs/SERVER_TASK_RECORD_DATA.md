# 서버 작업 요청: Record DB(데이터함) 검색을 moai-code에서 사용 (API-키)

> 대상: **open-moai 서버 팀.** moai-code CLI 가 "데이터함"(Record DB)의 조회 가능 테이블을
> 목록·스키마로 받고, LLM 이 생성한 **읽기전용 SQL** 을 실행해 표로 받는다.
> 패턴은 이미 배포된 `/api/v1/knowledge/search`(문서함)와 동일 — **기존 세션용 record-collections
> 로직 + `/api/v1/search` 의 API-키 인증 껍데기** 조합.
> 근거: open-moai 소스 직접 조사(2026-07-15). 경로/함수명은 실제 코드 기준.

---

## 1. 배경 — 왜 새 엔드포인트가 필요한가

- "데이터함" 실체 = **Record DB(native collections)** + SQL 쿼리. 웹앱 흐름(`lib/agent/tools/record-db-tool.ts`):
  `컬렉션 목록 → 각 테이블 스키마 → LLM 이 SELECT 생성 → 실행 → {row_count, markdown_table}`.
- 기존 엔드포인트는 **세션 인증**(`requireAuth`)이라 API-키(Bearer) CLI 는 호출 불가:
  - `GET /api/record-collections` (목록)
  - `POST /api/record-collections/query` (SQL 실행 → Python `/api/data-query/execute` 프록시)
  - Python `/api/data-query/schema/{table}` (스키마+샘플)
- `/api/v1/*` 에는 record 관련 엔드포인트가 **없음** → API-키 버전 신규 필요.

---

## 2. ⚠️ 보안 요구사항 (필수 — 현재 코드의 실제 구멍)

**현재 `app/api/record-collections/query/route.ts` 는 raw SQL 을 SQL 검증 없이 그대로 Python 으로
프록시한다.** 읽기전용 가드(`lib/security/sql-injection-detector.ts` 의 `detectSqlInjection` +
`checkDangerousKeywords`)는 어드민 경로(`app/api/database/query/route.ts`)에만 물려 있고 이 경로엔
없다. 신규 `/api/v1` 엔드포인트는 아래를 **반드시** 갖춰야 한다:

1. **`validateApiKey`** → `keyInfo.userId` 추출 (미인증 401 — `/api/v1/search` 와 동일 형식).
2. **`collection_id` 권한 검증** — 요청의 `collection_id` 가 그 `userId` 에 링크된 컬렉션인지 확인.
   (`getAgentNativeCollections` 의 `linkedIds` 필터 재사용.) **비링크 컬렉션 → 403.**
3. **읽기전용 SQL 강제** — Python 으로 넘기기 전에 TS 에서:
   ```ts
   import { detectSqlInjection, checkDangerousKeywords } from '@/lib/security/sql-injection-detector'
   const inj = detectSqlInjection(sql)            // 인젝션 패턴
   const kw  = checkDangerousKeywords(sql, false) // allowWrite=false → SELECT/EXPLAIN/SHOW/DESCRIBE/PRAGMA 만
   if (!inj.safe || !kw.safe) return 400 { error: 'read-only SELECT queries only' }
   ```
4. **구조적 격리 확인(운영)** — Python `/api/data-query/execute` 가 `collection_id` 로 **해당 컬렉션의
   테이블만** 접근 가능하도록 격리하는지 확인. (프롬프트 인젝션이 `SELECT ... FROM 타조직_테이블` 을
   생성해도 구조적으로 실패해야 함.) 격리가 없으면 이번 작업에 포함.
5. `endpoint-policy.ts` 에 `/v1/record-collections` 를 **`chat` 권한**으로 매핑(문서함과 동일).

> 위협 모델: 엔드포인트는 API-키 뒤 → "URL 만 알면" 접근 불가. 진짜 위험은 (a) 키 소지자의 임의
> SELECT, (b) 프롬프트 인젝션으로 인한 타컬렉션 열람. → 1~4 로 방어.

---

## 3. 엔드포인트 A: `GET /api/v1/record-collections`  (조회 가능 테이블 + 스키마)

CLI `OrgDatasList` 가 호출. LLM 이 SQL 을 쓰려면 **스키마가 필수**이므로, 목록에 컬럼 상세를 포함한다
(`buildSchemaContext` 로직 재사용 — 테이블별 `data-query/schema/{table}` 결과 병합).

응답(200):
```jsonc
{
  "collections": [
    {
      "id": "col_abc",
      "name": "2025년 매출 실적",          // filename/표시명
      "table_name": "sales_2025",
      "totalRecords": 12840,
      "columns": [
        { "name": "month",   "type": "TEXT",    "sample": ["1월","2월"] },
        { "name": "revenue", "type": "INTEGER", "sample": [120500000, 98300000] },
        { "name": "region",  "type": "TEXT",    "sample": ["서울","부산"] }
      ]
    }
  ],
  "count": 1
}
```
- 그 `userId` 에 링크된 컬렉션만. 없으면 `{ "collections": [], "count": 0 }`.
- `columns[].sample` 은 각 컬럼 대표값 2~3개(값 사전/샘플로 LLM 이 값 형식을 알게). 없으면 생략 가능.

---

## 4. 엔드포인트 B: `POST /api/v1/record-collections/query`  (읽기전용 SQL 실행)

CLI `OrgDatas` 가 호출. LLM 이 §3 스키마를 보고 만든 **SELECT** 를 실행.

요청:
```jsonc
POST /api/v1/record-collections/query
Authorization: Bearer moai-<...>
{ "collection_id": "col_abc", "sql": "SELECT region, SUM(revenue) FROM sales_2025 GROUP BY region", "maxRows": 100 }
```
- `sql` 없음 → 400 `{ "error": "sql is required" }`
- 비-SELECT/위험 키워드 → 400 `{ "error": "read-only SELECT queries only" }` (§2-3)
- `collection_id` 비링크 → 403
- `maxRows` 서버 클램프(기본 100, 최대 500)

응답(200):
```jsonc
{
  "columns": ["region", "sum_revenue"],
  "rows": [
    ["서울", 4640000000],
    ["부산", 1260000000]
  ],
  "rowCount": 2,
  "truncated": false          // maxRows 로 잘렸으면 true
}
```
> 참고: 기존 웹앱 프록시는 `{row_count, markdown_table}` 를 돌려주지만, CLI 는 **구조적 데이터
> (columns/rows)** 를 받아 자체 렌더(표) + 후속 처리(XlsxCreate 로 차트 파일 등)에 쓰므로 위 형태를
> 권장. markdown_table 만 가능하면 그것도 수용하되, columns/rows 가 있으면 CLI 활용도가 높다.

---

## 5. moai-code(클라이언트) 계약 요약

- `OrgDatasList` → `GET {baseUrl}/record-collections` → 위 §3.
- `OrgDatas` → `POST {baseUrl}/record-collections/query` → 위 §4. (LLM 이 §3 스키마 기반 SELECT 작성)
- 두 툴 모두 `IsReadOnly` (서버가 SELECT 만 허용). 응답은 **untrusted 외부 데이터 경계**
  (`<system-reminder>`)로 감싸 모델에 주입(프롬프트 인젝션 방어).
- 동작 연쇄: `OrgDatasList`(스키마) → LLM SQL 작성 → `OrgDatas`(실행) → (선택) `XlsxCreate` 로 차트.

---

## 6. 수용 기준 (서버 QA)

1. 유효 CLI 키 → 링크된 컬렉션 목록+스키마 반환. 비링크 컬렉션 미포함.
2. `OrgDatas` 에 **SELECT** → 200 + columns/rows. **INSERT/UPDATE/DELETE/DROP/ALTER/TRUNCATE →
   400**(실행 안 됨). `;` 다중문·`UNION SELECT` 인젝션 패턴 → 400.
3. 타 사용자/타 조직 컬렉션 `collection_id` → 403. 그 컬렉션 밖 테이블 참조 SQL → 실패(격리).
4. 무효 키 → 401(`Expected 'Bearer <api_key>'` 형식). `sql` 공백 → 400.
5. `maxRows` 초과 시 서버 클램프 + `truncated:true`.
6. 응답 필드가 §3/§4 와 정확히 일치(내부 필드·원시 스키마 원문 노출 금지).

---

## 7. 재사용 자산 (서버 구현 참고)

| 필요 | 기존 자산 |
|---|---|
| API-키 인증 껍데기 | `app/api/v1/search/route.ts` 복사 |
| 권한 매핑 | `endpoint-policy.ts` 에 `/v1/record-collections`=chat |
| 컬렉션 목록·권한 필터 | `getAgentNativeCollections`(`linkedIds`) |
| 스키마+샘플 | `buildSchemaContext` / Python `/api/data-query/schema/{table}` |
| SQL 실행 프록시 | `app/api/record-collections/query/route.ts` (여기에 §2 SQL 가드 추가) |
| **읽기전용 SQL 검증** | `lib/security/sql-injection-detector.ts`(이미 존재 — 이 경로에 물리기만) |

> 신규 코드 소규모. 핵심은 "**세션→API-키 인증 교체 + 지금 빠진 SQL 읽기전용 가드 추가**".
