# moai-code ↔ open-moai 서버 API: 데이터함(Record DB) + 이미지 생성

> **상태: 서버 구현 완료 (2026-07-17, vip.bccard.ai 배포).** 아래는 **확정된 클라이언트 계약**이다.
> 최초 요청서(2026-07-15) 대비 **권한 모델이 바뀌었다** — §2 를 반드시 읽을 것.
> 인증·경로 패턴은 기존 `/api/v1/knowledge/search`(문서함)와 동일.

---

## 1. 제공 엔드포인트 요약

| 엔드포인트 | 메서드 | 용도 | CLI 툴 |
|---|---|---|---|
| `/api/v1/record-collections` | GET | 조회 가능 테이블 + 스키마 | `OrgDatasList` |
| `/api/v1/record-collections/query` | POST | 읽기전용 SQL 실행 | `OrgDatas` |
| `/api/v1/images/generations` | POST | 이미지 생성(b64) | `ImageCreate` (신규) |

- 전부 **Bearer API 키** 인증(`Authorization: Bearer moai-<...>`). 세션/CSRF 없음.
- 권한 매핑: 세 경로 모두 **`chat` 권한**. 기존 CLI 키(`permissions:['chat','models']`)로 그대로 호출 가능.
- 무효 키 → **401**, 형식: `Expected 'Bearer <api_key>'`.

### 1.1 🐞 함께 고친 버그: `/api/v1/organizations` 가 항상 401 (260717)

`OrgList` / `OrgDocsUpload`(orgId 자동 해소)가 **유효한 API 키로도 계속 401
`Authentication required`** 를 내던 문제를 수정했다.

- 원인: `/api/v1/organizations` 는 라우트와 권한정책은 있었지만 서버 `proxy.ts` 의
  **세션 게이트 면제 목록(publicPaths/csrfExempt)에 등록이 누락**돼 있었다. 그래서 Bearer 키를
  검증하는 라우트에 도달하기 전에 **세션 미들웨어가 먼저 401** 로 끊었다.
- 증상 특징: **재로그인해도 해결 안 됨** (CLI 는 세션 쿠키가 아니라 Bearer 키를 쓰므로 무관).
  에러 메시지가 `Expected 'Bearer <api_key>'` 가 아니라 `Authentication required` 면 이 유형이다.
- 조치: proxy 면제 목록에 `/api/v1/organizations` 추가 → 배포 완료.
- **클라이언트 조치 불필요.** 서버 배포 후 기존 코드 그대로 동작한다.

### 1.2 🐞 함께 고친 버그: 한글 파일명이 `=?utf-8?B?...?=` 로 저장됨 (260717)

CLI 로 한글 파일명을 업로드하면 문서함에 제목·파일명이
`=?utf-8?B?6riI7Jy16raMIEFJIOqxsOuyhOuEjOyKpCDsoJXssYUg7LSI7JWILmRvY3g=?=`
처럼 노출되던 문제를 수정했다. (디코드하면 `금융권 AI 거버넌스 정책 초안.docx`)

- **원인(클라이언트 측)**: .NET 의 `MultipartFormDataContent` / `ContentDispositionHeaderValue.FileName`
  은 비ASCII 파일명을 **RFC 2047 encoded-word**(`=?charset?B?base64?=`)로 인코딩한다.
  근본 원인은 .NET 의 `HttpRuleParser.DefaultHttpEncoding` 이 UTF-8 이 아니라 **ISO-8859-1** 인 것
  ([dotnet/runtime#22996](https://github.com/dotnet/runtime/issues/22996), 2017~ 미해결).

- **표준은 뭐라고 하나 (RFC 7578 원문 확인)**:
  - **구 RFC 2388 은 encoded-word 를 권고했었다** → .NET 이 이걸 따르는 것. 즉 "틀린 구현"이라기보다 **레거시**.
  - **RFC 7578(2015) 이 RFC 2388 을 폐기(obsoletes)** 하고 그 권고를 없앴다. 현행 송신자 기준:
    > "Some commonly deployed systems use multipart/form-data with file names directly encoded
    > including octets outside the US-ASCII range. **The encoding used for the file names is
    > typically UTF-8**" (§4.2)
    → **원시 UTF-8 옥텟이 현행 baseline**(브라우저가 하는 방식). percent-encoding 은 MAY(§4.2).
  - ⚠️ **`filename*`(RFC 5987) 은 쓰면 안 된다** — §4.2 원문:
    > "NOTE: The encoding method described in [RFC5987], which would add a "filename*" parameter
    > to the Content-Disposition header field, **MUST NOT be used**."
    (이 문서 이전 버전에서 `FileNameStar` 사용을 권했으나 **표준 위반이라 철회**한다.)
  - **수신자(서버) 는 레거시를 관용하라고 명시**돼 있다 — §5.1.3 원문:
    > "some multipart/form-data generators might have followed the previous advice of [RFC2388]
    > and used the "encoded-word" method of encoding non-ASCII values, as described in [RFC2047]"
    → 파서는 이 변종들을 인지하고 있어야 한다.

- **결론: 양쪽 다 해야 하고, 서로 배타적이지 않다.**
  - **서버(완료·배포됨)**: encoded-word 를 방어적으로 디코드
    (`lib/utils/mime-encoded-word.ts` → `lib/knowledge/org-docs-service.ts`). 웹/CLI 공통.
    RFC 7578 §5.1.3 이 요구하는 수신자 관용에 해당. **지금 상태로 CLI 는 이미 정상 동작한다.**
    기존에 깨져 저장된 문서 1건도 복구 완료.
  - **클라이언트(권장, 급하지 않음)**: 파일명을 **원시 UTF-8 옥텟**으로 보내면 현행 표준에 맞는다.
    `FileName` 프로퍼티를 그대로 쓰면 .NET 이 RFC2047 로 인코딩하므로, 헤더를 직접 구성하는
    방식(예: `Headers.TryAddWithoutValidation("Content-Disposition", ...)`)이 필요하다.
    **`FileNameStar`/`filename*` 는 RFC 7578 상 금지이므로 쓰지 말 것.**
  - 서버 디코더는 평문 UTF-8 파일명을 **그대로 통과**시키므로(encoded-word 패턴이 없으면 no-op),
    클라이언트가 UTF-8 로 전환해도 **충돌 없이 공존**한다.

---

## 2. ⚠️ 권한 모델 변경 (최초 요청서와 다름 — 중요)

최초 요청서는 "**userId 에 링크된 컬렉션**"(`getAgentNativeCollections` 의 `linkedIds`) 기준이었다.
그러나 2026-07-17 서버에 **데이터 카테고리 기반 접근제어(모델 A)** 가 도입되면서, 기존 "조직 RAG 배정"
(에이전트 링크) UI 는 **제거**됐다. 따라서 링크 기준은 더 이상 유지보수되지 않는다.

**확정 규칙 — 카테고리 공개범위:**

| 카테고리 공개범위 | 노출 대상 |
|---|---|
| `company` (전사) | 모든 조직 |
| `organization` (조직) | 그 카테고리에 지정된 조직 소속자만 |
| `private` / 미분류 | **비노출** (관리자 전용) |

- API 키의 `userId` → **그 사용자의 소속 조직들** → 위 규칙으로 보이는 컬렉션의 **합집합**.
- 웹 데이터 뷰 · 조직 AI 비서 · CLI 가 **모두 같은 규칙**을 쓴다(서버 `lib/knowledge/record-db-org.ts` 단일 소스).
- 실무 영향: **관리자가 `admin/record-db` 에서 컬렉션에 카테고리를 배정해야** CLI 에 보인다.
  미분류 컬렉션은 CLI 에서 안 보이는 게 정상이다.
- 소속 조직이 없거나 보이는 컬렉션이 없으면 → `{ "collections": [], "count": 0 }` (GET) / **403** (query).

---

## 3. `GET /api/v1/record-collections`

조회 가능 테이블 + **스키마**(LLM 이 SQL 을 쓰려면 필수).

```
GET /api/v1/record-collections
Authorization: Bearer moai-<...>
```

응답(200):
```jsonc
{
  "collections": [
    {
      "id": "cache_cafe_store_home_brand",
      "name": "카페 점포 홈브랜드",        // 표시명(filename)
      "table_name": "cache_cafe_store_home_brand",
      "totalRecords": 12840,
      "columns": [
        { "name": "점포소재지", "type": "character varying", "sample": ["서울","부산","대구"] },
        { "name": "총이용금액", "type": "bigint" },
        { "name": "총이용건수", "type": "bigint" }
      ]
    }
  ],
  "count": 1
}
```
- `columns[].type` = PostgreSQL `information_schema` 의 `data_type`.
- `columns[].sample` = **카디널리티 낮은 컬럼의 실제 값** 최대 3개(서버의 value_dictionary). 없으면 필드 생략.
  → WHERE 절에 쓸 실제 값 형식을 LLM 에 알려주는 용도.
- 스키마 조회 실패 시 컬럼명만 `{ name, type: "unknown" }` 로 폴백.

---

## 4. `POST /api/v1/record-collections/query`

§3 스키마를 보고 LLM 이 만든 **SELECT** 실행.

```jsonc
POST /api/v1/record-collections/query
Authorization: Bearer moai-<...>
{
  "collection_id": "cache_cafe_store_home_brand",   // 선택. 주면 그 테이블로 질의 범위 한정
  "sql": "SELECT 점포소재지, SUM(총이용금액) AS 금액 FROM cache_cafe_store_home_brand GROUP BY 점포소재지",
  "maxRows": 100
}
```

응답(200):
```jsonc
{
  "columns": ["점포소재지", "금액"],
  "rows": [
    ["서울", 4640000000],
    ["부산", 1260000000]
  ],
  "rowCount": 2,
  "truncated": false
}
```

### 4.1 파라미터
- `sql` **필수**. 공백/누락 → 400 `{ "error": "sql is required" }`
- `collection_id` 선택. **주면 그 컬렉션 테이블만** 참조 가능(범위 축소). 안 주면 접근 가능한 전체 테이블 대상.
- `maxRows` 기본 **100**, 최대 **500** (서버 클램프). `rowCount >= maxRows` 면 `truncated: true`.

### 4.2 서버 가드 (요청 전에 클라가 알아야 할 것)
1. **읽기전용만**: `SELECT` / `WITH ... SELECT` 만. INSERT/UPDATE/DELETE/DROP/ALTER/TRUNCATE →
   **400** `{ "error": "read-only SELECT queries only" }`
2. **인젝션 패턴 차단**: `;` 다중문, `UNION SELECT` 주입 패턴 등 → **400** (동일 메시지)
3. **테이블 화이트리스트(격리)**: SQL 이 참조하는 `FROM`/`JOIN` 테이블이 전부 접근 허용 집합 안에 있어야 함.
   아니면 **403** `{ "error": "not permitted to query table(s): <이름>" }`
   → 타 조직 테이블은 이름을 알아도 조회 불가. **`WITH` CTE 별칭은 허용**(실제 테이블 아님).
4. 테이블 참조가 아예 없으면 → 400 `{ "error": "query must reference an accessible table" }`
5. `collection_id` 가 비허용 → **403** `{ "error": "collection not found or not permitted" }`
6. 접근 가능한 컬렉션이 하나도 없으면 → **403** `{ "error": "no accessible data collections" }`
7. SQL 문법/실행 오류 → 400 `{ "error": "SQL error: ..." }`

> 서버는 `LIMIT` 이 없으면 자동으로 `LIMIT {maxRows}` 를 붙이고, `statement_timeout=30s` 를 건다.
> 대용량 테이블(1억건대)에 풀스캔 집계를 던지면 타임아웃될 수 있으니, 가능하면 캐시성 테이블을 쓰거나
> WHERE 로 좁힐 것.

### 4.3 클라이언트 권장 동작
- `OrgDatasList`(스키마) → LLM SQL 작성 → `OrgDatas`(실행) → (선택) `XlsxCreate` 로 차트/파일.
- 두 툴 모두 `IsReadOnly`.
- 응답은 **untrusted 외부 데이터 경계**(`<system-reminder>`)로 감싸 모델에 주입(프롬프트 인젝션 방어).
- 403 `not permitted to query table(s)` 를 받으면 → LLM 에 "허용 테이블 목록"(§3 결과)을 다시 주고
  재작성시키는 게 UX 상 유리.

---

## 5. `POST /api/v1/images/generations`  (신규)

OpenAI images API 호환 형태. **b64_json 전용**.

```jsonc
POST /api/v1/images/generations
Authorization: Bearer moai-<...>
{
  "prompt": "은은한 조명의 카페 인테리어, 사진풍",
  "model": "gemini-2.5-flash-image",   // 선택. 모델 PK 또는 모델명. 미지정 시 서버 기본 이미지 모델
  "n": 1,                               // 선택. 현재 1 만 지원
  "response_format": "b64_json"        // 선택. b64_json 만 지원
}
```

응답(200):
```jsonc
{
  "created": 1784188711,
  "data": [
    {
      "b64_json": "iVBORw0KGgoAAAANSUhEUg...",
      "mime_type": "image/png",
      "revised_prompt": "생성된 이미지에 대한 모델의 설명 텍스트"   // 없을 수 있음
    }
  ],
  "model": "gemini-2.5-flash-image"
}
```

### 5.1 왜 URL 이 아니라 b64 인가
서버의 이미지 서빙 경로(`/api/images/*`)는 **세션 인증**이라 Bearer 키만 가진 CLI 가 URL 을 받아도
읽을 수 없다. 그래서 **base64 로 직접 반환**한다. CLI 는 받은 b64 를 디코드해 파일로 저장하면 된다.
(생성 이미지는 서버 갤러리 스토리지에도 함께 저장된다.)

### 5.2 오류
| 상황 | 코드 | 응답 |
|---|---|---|
| `prompt` 누락 | 400 | `{ "error": "prompt is required" }` |
| `n != 1` | 400 | `{ "error": "only n=1 is supported" }` |
| `response_format != b64_json` | 400 | `{ "error": "only response_format='b64_json' is supported" }` |
| 모델 없음/이미지 미지원 | 400 | `{ "error": "model '<x>' not found or does not support image generation" }` |
| 서버에 활성 이미지 모델 없음 | 503 | `{ "error": "no image generation model is enabled on this server" }` |
| 생성 실패 | 502 | `{ "error": "image generation failed" }` (또는 모델 메시지) |

### 5.3 클라이언트 주의
- 생성은 수 초~수십 초 걸린다. 서버 `maxDuration=300s`. CLI 타임아웃을 넉넉히(≥120s) 둘 것.
- b64 페이로드가 크다(수 MB). 스트리밍 없이 한 번에 오므로 메모리/버퍼 여유 필요.
- 사용 가능한 이미지 모델 목록을 미리 알고 싶으면 관리자에게 문의(현재 `/api/v1/models` 는 채팅 모델 기준).

---

## 6. 수용 기준 (서버 QA — 구현 반영)

1. 유효 CLI 키 → **카테고리 공개범위**로 보이는 컬렉션 목록+스키마 반환. 비가시 컬렉션 미포함. ✅
2. `OrgDatas` 에 SELECT → 200 + columns/rows. INSERT/UPDATE/DELETE/DROP/ALTER/TRUNCATE → 400(실행 안 됨).
   `;` 다중문·`UNION SELECT` 인젝션 → 400. ✅
3. 비가시 컬렉션 `collection_id` → 403. 허용 밖 테이블 참조 SQL → **403**(TS 화이트리스트 격리). ✅
   > 참고: Python `/api/data-query/execute` 는 `collection_id` 를 **로깅용으로만** 쓰고 테이블을 격리하지
   > 않는다(최초 요청서 §2-4 가 지적한 구멍). 격리는 Next 라우트의 화이트리스트 가드가 담당한다.
4. 무효 키 → 401. `sql` 공백 → 400. ✅
5. `maxRows` 초과 → 서버 클램프(최대 500) + `truncated:true`. ✅
6. 응답 필드가 §3/§4/§5 와 정확히 일치(내부 필드·원시 스키마 원문 미노출). ✅

---

## 7. 서버 구현 위치 (참고)

| 항목 | 파일 |
|---|---|
| 인증 가드 | `lib/openai-api/v1-guard.ts` (`guardV1`/`finalizeV1`/`optionsV1`) |
| 권한 매핑 | `lib/openai-api/endpoint-policy.ts` (`/v1/record-collections`, `/v1/images` = `chat`) |
| 세션/CSRF 면제 | `proxy.ts` (csrfExempt + publicPaths 프리픽스) |
| **가시성 단일 소스** | `lib/knowledge/record-db-org.ts` (`resolveUserRecordCollections`, `resolveOrgRecordCollections`) |
| 스키마 DTO + SQL 가드 | `lib/knowledge/record-db-service.ts` (`buildCollectionSchemaDtos`, `guardReadOnlySql`, `executeRecordQuery`) |
| 라우트 | `app/api/v1/record-collections/route.ts`, `.../query/route.ts` |
| 이미지 서비스 | `lib/images/v1-image-service.ts` (`generateImageB64`) |
| 이미지 라우트 | `app/api/v1/images/generations/route.ts` |
