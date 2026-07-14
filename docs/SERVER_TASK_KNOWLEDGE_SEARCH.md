# 서버 작업 요청: `/api/v1/knowledge/search` 를 API-키 인증 체인으로 이동

> **상태: ✅ 해결됨 (2026-07-14 확인).** 서버팀이 이 라우트를 `validateApiKey` 체인으로 이동
> (세션/CSRF 겹 제거 + endpoint-policy `/v1/knowledge`=chat 매핑). 실측 확인:
> - 미인증 요청 → `/api/v1/search` 와 동일한 401 Bearer 응답 (CSRF 아님).
> - moai-code `OrgDocs` 툴 엔드투엔드 성공: API 키로 개인(`personal`)·조직(`organization`)
>   문서가 계약대로(`results[]`: title/snippet/scope/score) 반환됨.
>
> 아래는 수정 전 진단 기록(참고용 보존).

## 한 줄 요약
`POST /api/v1/knowledge/search` 가 **브라우저용(세션 쿠키 + CSRF) 미들웨어 스택**에 얹혀 있어,
moai-code CLI(Bearer API-키)로는 접근이 불가능합니다. `/api/v1/chat/completions`,
`/api/v1/search` 와 **동일한 `validateApiKey` 체인**으로 옮겨야 합니다.

## 재현 (인증 없이 프로덕션 실측, `vip.bccard.ai`)

| 경로 | 응답 | 의미 |
|---|---|---|
| `POST /api/v1/search` | `401` · `{"error":{"message":"Missing or invalid authorization header. Expected 'Bearer <api_key>'"}}` | ✅ **API-키 체인** — CSRF 없이 인증 단계까지 도달 |
| `POST /api/v1/knowledge/search` | `403` · `{"error":"Cross-origin request denied","code":"CSRF_ORIGIN_MISMATCH"}` | ❌ **CSRF 미들웨어**가 인증 전에 차단 |
| `POST /api/knowledge/search` | `403` · 동일 CSRF | (기존 세션용 라우트) |

`Origin`/`Referer` 헤더를 서버 자기자신으로 맞춰 CSRF를 통과시켜도:

```
POST /api/v1/knowledge/search   (Origin: https://vip.bccard.ai)
→ 401 {"error":"Authentication required","requiresAuth":true,"code":"AUTHENTICATION_REQUIRED"}
```

즉 이 라우트는 **(1) CSRF 오리진 검사 + (2) 세션/쿠키 인증** 두 겹의 브라우저 전용 미들웨어를
거칩니다. `/api/v1/search` 가 요구하는 `Bearer <api_key>` (API-키 인증)와 다릅니다.
클라이언트(CLI)는 세션 쿠키가 없으므로 우회 불가 — **서버에서 고쳐야 합니다.**

## 원인 (추정)
`/api/v1/knowledge/search` 를 신설하면서 기존 세션용 `/api/knowledge/search` 핸들러/미들웨어를
재사용해, `validateApiKey` 대신 세션+CSRF 스택이 그대로 딸려온 것으로 보입니다.

## 해야 할 일
1. `/api/v1/knowledge/search` 라우트를 `/api/v1/chat/completions`·`/api/v1/search` 와 **같은
   미들웨어 체인**(`validateApiKey` → API-키로 userId 확인)에 등록.
2. 이 라우트에서 **CSRF 오리진 검사와 세션 쿠키 인증을 제거**.
   - Bearer 토큰 API 요청은 구조적으로 CSRF 대상이 아님(쿠키를 자동 전송하지 않으므로).
3. `endpoint-policy.ts` 에서 `/v1/knowledge` 를 `chat` 과 동일 권한(scope)으로 매핑.
4. API-키로 확인한 `userId` 를 그대로 `resolveKnowledgeScopeTargets(userId, scope)` 에 전달
   (세션 userId 가 아니라).

## 요청/응답 계약 (moai-code 가 보내고 기대하는 형식)

요청:
```http
POST /api/v1/knowledge/search
Authorization: Bearer <api_key>
Content-Type: application/json

{ "query": "휴가 정책", "scope": "all", "topK": 10 }
```
- `scope`: `"personal"` | `"organization"` | `"all"` (기본 `all`)
- `topK`: 1–50 (기본 10)

응답(200):
```json
{
  "results": [
    { "title": "...", "snippet": "...", "scope": "organization",
      "source": "...", "documentId": "doc_1", "score": 0.87,
      "url": "https://vip.bccard.ai/knowledge/documents/doc_1" }
  ],
  "count": 1
}
```

## 수용 기준
- [ ] `Bearer <api_key>` 만으로 `POST /api/v1/knowledge/search` 가 200 반환(세션 쿠키/Origin 헤더 불필요).
- [ ] 인증 없는 요청은 `/api/v1/search` 와 **동일한 401 형식**(`Expected 'Bearer <api_key>'`) 반환 —
      403 CSRF 가 아니라.
- [ ] `scope=organization` 은 요청자가 소속된 조직 문서만, `scope=personal` 은 요청자 본인 private
      문서만 반환(서버측 접근 제어).
- [ ] 응답이 위 계약 형식과 일치.

상세 설계는 `PLAN_OPENMOAI_DOCS.md` 참고.
