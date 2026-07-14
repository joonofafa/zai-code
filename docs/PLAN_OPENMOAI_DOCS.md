# 작업지시서 — open-moai 조직 문서함(Knowledge/RAG) 검색을 moai-code에서 사용

> **상태: ✅ 완료 (2026-07-14).** 서버가 `/api/v1/knowledge/search` 를 API-키 체인으로 배포했고
> (`SERVER_TASK_KNOWLEDGE_SEARCH.md`), RAG 스코어링도 수정됨(`SERVER_TASK_RAG_QUALITY.md`).
> moai-code `OrgDocs` 툴 엔드투엔드 동작 확인. **이 문서는 구현 완료된 설계 기록(참고용 보존).**

> 목표: moai-code(외부 코딩 에이전트)가 **로그인한 사용자의 개인 문서 + 소속 조직 문서함**을
> API 키로 검색해 코딩 컨텍스트로 활용한다. 로그인·모델선택은 이미 연동됨(`moai login`).
>
> 대상: **open-moai 서버 팀**. 클라이언트(moai-code)의 `OrgDocs` 툴과 계약 테스트는 구현 완료됐으며,
> 서버는 아래 §3~§4 계약에 맞는 `/api/v1/knowledge/search` 배포가 필요하다.
> 작성 근거: open-moai 소스 직접 조사(2026-07-03). 경로/함수명은 실제 코드 기준.

---

## 1. 배경 — 왜 새 엔드포인트가 필요한가

- 조직 문서함의 실체는 `knowledge` + `rag`(하이브리드 임베딩 검색)이고, 접근 스코프는
  `rag_documents.visibility = 'private' | 'organization'` 으로 **데이터 모델에 이미 구현**돼 있다.
- 통합 검색 로직도 이미 있다: `app/api/knowledge/search/route.ts`
  (`prepareKnowledgeSearchQuery` → `resolveKnowledgeScopeTargets` → `performHybridSearch`).
- **문제**: 이 엔드포인트는 **세션 쿠키 인증**(`withUserAudit`)이라, **API 키(`Bearer moai-…`)를
  쓰는 moai-code는 호출할 수 없다.** (`/api/v1/search`는 API 키 인증이지만 **웹검색** 전용이다.)
- 따라서 **기존 검색 로직을 그대로 재사용하되 API 키 인증 껍데기만 씌운** 새 엔드포인트가 필요하다.
  이미 있는 `/api/v1/search`(웹검색)와 완전히 같은 미들웨어 골격을 쓰면 된다.

---

## 2. 요구 사항 — 신규 엔드포인트

### `POST /api/v1/knowledge/search`

기존 `/api/v1/search/route.ts`(웹검색)의 **미들웨어 골격을 복사**하고, 검색 본체만
`/api/knowledge/search`의 로직으로 교체한다.

#### 2-1. 인증·게이트 (기존 `/api/v1/*`와 동일, 그대로 재사용)
`lib/openai-api/middleware.ts` 체인을 순서대로:
1. `validateApiKey(request)` → `{ isValid, keyInfo, apiManagement }`. **무효 시 401.**
2. `checkIpFilter` → 403
3. `checkEndpointPermission(keyInfo, endpoint, apiManagement)` → 403 (아래 2-2 권한 매핑)
4. `checkRateLimit(keyInfo.id, keyInfo, apiManagement)` → 429
5. `applySecurityHeaders` / `setCorsHeaders` / `apiLogger`·`webhookService`·`alertService` — `/v1/search`와 동일

> `api_management` 게이트(`apiEnabled`/`openaiCompatible`/`apiKeyEnabled`)는 `validateApiKey`가 처리.

#### 2-2. 권한 매핑 (`lib/openai-api/endpoint-policy.ts`)
`/v1/search`와 동일하게 **`chat` 권한**으로 매핑한다. CLI 키의 기본 권한이 `['chat','models']`
(로그인 시 발급)이므로 별도 권한 부여 없이 그대로 통과한다.

```ts
// endpoint-policy.ts 규칙 배열에 추가 (fallback 앞, '/v1/chat'보다 구체적 패턴 우선)
{
  pattern: '/v1/knowledge',
  permission: 'chat',
  matchType: 'prefix',
  description: 'Knowledge/document repository search for external coding agents',
},
```
`getOpenAiEndpointFromRequest`가 `/v1/knowledge/search`를 위 패턴에 매칭해야 함(`/v1/search`와 동형).

#### 2-3. 사용자 스코프 해석 (핵심 — 접근제어는 여기서 자동으로 끝남)
`keyInfo`에서 **인증된 사용자 id**를 얻는다(`api_keys.user_id`). 이 `userId`로:
- `resolveKnowledgeScopeTargets(uow.knowledge, userId, 'personal')` → **본인 private 문서만**
- `resolveKnowledgeScopeTargets(uow.knowledge, userId, 'organization')` → **본인이 속한 모든 조직의
  문서 union** (`getUserOrganizations(userId)` 기반, 비소속 조직은 애초에 타깃에 없음)

> 접근제어를 위한 **추가 로직 불필요.** 사용자가 볼 수 없는 문서는 스코프에 포함되지 않는다.
> (요청 `scope`로 personal/organization/all 선택 — 아래 §3)

#### 2-4. 검색 실행 (기존 로직 재사용)
`/api/knowledge/search`와 동일:
```ts
const uow = await getUnitOfWork()
const { vectorStoreConfig, queryExpansion, queryEmbeddings } =
  await prepareKnowledgeSearchQuery(uow, query)

// scope에 따라 targets 수집 (personal / organization)
for (const target of searchTargets) {
  const hits = await performHybridSearch({
    vectorStoreConfig,
    collectionName: target.name,
    queryEmbeddings,
    queryTexts: queryExpansion.allQueries,
    topK,
    filter: target.filter,
    userId: target.usePostgresFts ? userId : undefined,
  })
  // dedupe(postId/knowledgeId/documentId) → score 높은 것 유지
}
// score desc 정렬 → topK slice
```
설정 미구성/벡터스토어 없음 → **400 `{ "error": "knowledge search not configured" }`** 로 통일
(웹검색의 `web search not configured`와 동형).

---

## 3. 요청 계약 (고정)

```jsonc
POST /api/v1/knowledge/search
Authorization: Bearer moai-<...>
Content-Type: application/json

{
  "query": "결제 취소 API 재시도 정책",   // (필수) 검색어
  "scope": "all",                          // (선택) "personal" | "organization" | "all"  (기본 "all")
  "topK": 8,                               // (선택) 1..50, 기본 10
  "organizationId": "org_123"              // (선택) organization 스코프에서 특정 조직만
}
```
- `query` 없음 → **400 `{ "error": "query is required" }`**
- `topK`는 서버에서 `min(max(n,1),50)` 클램프

---

## 4. 응답 계약 (고정 — 클라이언트가 이 형태에 의존)

### 200 OK
```jsonc
{
  "results": [
    {
      "title": "결제 취소 처리 가이드",     // 문서/포스트 제목 (metadata.title 등)
      "snippet": "취소 요청은 최대 3회 …",  // 매칭 청크 발췌 (content, 서버에서 ~500자 트렁케이트)
      "scope": "organization",              // "personal" | "organization"
      "source": "bccard-payments-docs",     // 컬렉션/문서 출처 식별자
      "documentId": "doc_abc123",
      "score": 0.87,                        // 0..1 유사도
      "url": "https://vip.bccard.ai/knowledge/documents/doc_abc123"  // (선택) 없으면 null
    }
  ],
  "count": 1
}
```
매핑 규칙(`HybridSearchResult{ id, documentId, content, score, metadata }` → 위):
- `title` ← `metadata.title` (없으면 `source`/문서명 폴백)
- `snippet` ← `content` 앞부분 트렁케이트(~500자)
- `scope` ← target 유형(personal→`personal`, organization→`organization`)
- `source` ← `collectionName` 또는 `metadata.collection`
- `url` ← 가능하면 문서함 딥링크, 불가하면 `null`

### 오류
| 상황 | 코드 | 바디 |
|---|---|---|
| 키 무효/게이트 off | 401 | `validateApiKey`의 표준 OpenAI 오류 |
| IP 차단 | 403 | 미들웨어 표준 |
| 권한 없음(`chat` 미보유 & endpointLimited) | 403 | permission_denied |
| 레이트리밋 | 429 | 미들웨어 표준 |
| `query` 없음 | 400 | `{ "error": "query is required" }` |
| 검색 미구성(벡터스토어/설정 없음) | 400 | `{ "error": "knowledge search not configured" }` |
| 내부 오류 | 500 | `{ "error": "Internal server error" }` |

---

## 5. moai-code(클라이언트) 계약 — 서버가 맞춰줘야 할 기대치

클라이언트는 로그인 시 저장한 `baseUrl`(= `https://vip.bccard.ai/api/v1`)에 대해
`POST {baseUrl}/knowledge/search` 를 `Authorization: Bearer {저장키}` 로 호출한다.
- 새 툴 `OrgDocs`(표기 "문서검색")로 에이전트가 코딩 중 자동 호출.
- 응답 `results`를 **untrusted 외부 데이터 경계(`<system-reminder>`)로 감싸** 모델에 주입(프롬프트 인젝션 방어).
- **위 §3/§4 스키마가 계약**이다. 필드명·타입이 바뀌면 클라이언트도 수정 필요하니, 확정 후 변경 시 공유.

---

## 6. 수용 기준 (Acceptance — 서버 QA 체크리스트)

1. 유효 CLI 키 + `scope:"all"` → 개인 private + 소속 조직 문서가 섞여 score순으로 반환.
2. `scope:"personal"` → **본인 private 문서만**, 조직 문서 미포함.
3. `scope:"organization"` → **본인 소속 조직 문서만**. **비소속 조직 문서는 절대 미반환**(핵심 보안).
4. 타 사용자의 private 문서가 응답에 **절대 안 섞임**(격리 검증).
5. 무효 키 → 401 / `query` 공백 → 400 / 벡터스토어 미구성 → 400(`knowledge search not configured`).
6. `apiKeyEndpointLimited=true` + 키에 `chat` 없음 → 403. `chat` 있으면 통과.
7. 레이트리밋 초과 → 429. 응답에 `results`/`count` 계약 유지(빈 결과라도 `{results:[],count:0}`).
8. 응답 필드가 §4와 정확히 일치(불필요한 내부 필드 노출 금지 — `metadata` 원문 그대로 흘리지 말 것).

---

## 7. 재사용 자산 요약 (서버 구현 참고)

| 필요 | 기존 자산 |
|---|---|
| 미들웨어 골격(인증/게이트/로깅/CORS) | `app/api/v1/search/route.ts` 복사 |
| 권한 매핑 | `lib/openai-api/endpoint-policy.ts` 규칙 추가 |
| 쿼리 임베딩/확장 | `prepareKnowledgeSearchQuery` (`lib/knowledge/search-runtime.ts`) |
| 개인/조직 스코프 해석 | `resolveKnowledgeScopeTargets(uow.knowledge, userId, scope)` |
| 하이브리드 검색 | `performHybridSearch` (`lib/rag/hybrid-search.ts`) |
| 결과 조립(dedupe/sort) | `app/api/knowledge/search/route.ts` 로직 그대로 |

> 사실상 "**`/api/knowledge/search`의 본체 + `/api/v1/search`의 인증 껍데기**" 조합이라 신규 코드는 소규모.

---

## 8. 범위 밖 / 후속

- **쓰기(업로드)**: 이번 지시서는 **검색(읽기) 전용.** 문서 업로드는 별도.
- **개인 메모(`/api/v1/memos`)**: 이미 API 키로 접근 가능 — 필요 시 클라이언트가 별도 툴로 붙일 수 있음(서버 작업 불필요).
- **딥링크 `url`**: 여유 없으면 1차엔 `null`로 내보내도 됨(클라이언트는 null 허용).
- **감사 로그**: `/api/v1/search`처럼 `apiLogger.logRequest/logResponse` 남겨 관측 유지.
