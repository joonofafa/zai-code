# 서버 작업 요청: 조직 문서 **원문 콘텐츠**를 API-키(Bearer)로 받는 v1 엔드포인트

> **상태: 요청 (2026-07-22).** MoAI Desktop(모드 B: 참조 문서로 새 문서 작성)에서
> 조직 문서를 **원문 통째**로 참조하려면 문서 콘텐츠를 Bearer API-키로 받아와야 합니다.
> 현재 콘텐츠 다운로드 경로는 **웹세션 전용**이라 API-키로는 `401` 입니다.

## 한 줄 요약
`GET /api/v1/knowledge/{id}?download=1` (Bearer API-키 인증) 를 추가해 **원본 파일 바이트**를
내려주세요. 로직·권한검사는 기존 세션용 라우트
`app/api/organizations/[id]/documents/[documentId]/route.ts` (`?download=1`) 를 그대로
재사용하되, **인증만 `validateApiKey`(v1 체인)** 로 바꾸면 됩니다.

## 배경 — 왜 필요한가
MoAI Desktop 은 API-키(Bearer)만 가진 클라이언트입니다. 모드 B 는 사용자가 고른 참조 문서의
**원문을 컨텍스트에 그대로 넣어** 새 문서를 만듭니다(청킹/임베딩 불필요).

- **로컬 파일**: 클라이언트가 직접 읽어 처리 — 문제 없음.
- **조직 문서**: 콘텐츠를 서버에서 받아와야 하는데, **받을 방법이 없습니다.**
  - 현재는 RAG 검색(`/v1/knowledge/search`) 의 **스니펫**만 참조로 씁니다(관련 조각만, 원문 전체 아님).
  - "이 문서 통째를 근거로" 하려면 **원문 콘텐츠 엔드포인트**가 필요합니다.

## 재현 — 현재는 API-키로 콘텐츠를 못 받음 (`vip.bccard.ai` 실측, 2026-07-22)

| v1(Bearer) 엔드포인트 | 콘텐츠 제공? | 실측 |
|---|---|---|
| `GET /api/v1/knowledge?orgId=…` (목록) | ❌ 메타데이터만 | 200, `items[]` 에 `downloadUrl` 포함 |
| `GET /api/v1/knowledge/{id}` | ❌ 메타 DTO만 | 200, 콘텐츠 없음 |
| `downloadUrl` = `GET /api/organizations/{orgId}/documents/{id}?download=1` | ✅ 원문 주지만 **세션 전용** | **`401` (Bearer 키로)** |

```bash
# 목록의 downloadUrl 을 Bearer API-키로 GET → 401
curl -s -o /dev/null -w "%{http_code}\n" \
  -H "Authorization: Bearer $MOAI_KEY" \
  "https://vip.bccard.ai/api/organizations/<orgId>/documents/65?download=1"
# → 401
```

즉 콘텐츠 다운로드 라우트는 `withAuthRoute`(세션/쿠키 인증) 스택이라, CLI/데스크톱의
Bearer API-키로는 통과 불가입니다. `/v1/knowledge/search` 를 API-키 체인으로 옮긴 것과 동일한
성격의 작업입니다(→ `SERVER_TASK_KNOWLEDGE_SEARCH.md`, 해결됨).

## 해야 할 일
1. **신규 라우트** `GET /api/v1/knowledge/{id}` 에 `?download=1` 분기를 추가 (또는 별도
   `/api/v1/knowledge/{id}/content`). 인증은 `/api/v1/chat/completions`·`/api/v1/knowledge/search`
   와 **같은 `validateApiKey`(Bearer)** 체인.
2. 처리 로직은 기존 세션 라우트
   `app/api/organizations/[id]/documents/[documentId]/route.ts` 의 `?download=1` 블록
   (원본 `originalContent`(base64) → attachment) 을 **그대로 재사용**.
3. 권한·가시성 검사도 **동일하게 fail-closed** 유지:
   - 요청자가 해당 org 멤버가 아니면 `403`.
   - `visibility !== 'organization'` 이면 **업로더 본인(또는 admin)만** 허용, 그 외 `403`.
   - 문서 없음/org 불일치 `404`, `originalContent` 없음 `404`.

## 제안 API 계약

```
GET /api/v1/knowledge/{id}?download=1&orgId=<organizationId>
Authorization: Bearer <api_key>
```

| 항목 | 값 |
|---|---|
| 인증 | Bearer API-키 (`validateApiKey`) |
| 쿼리 | `orgId` (필수), `download=1` |
| 성공 | `200` · 원본 바이트. `Content-Type`(원본 MIME), `Content-Disposition: attachment; filename*=UTF-8''<name>` |
| 오류 | `401`(키 없음/무효) · `403`(비멤버/비공개 타인) · `404`(문서 없음/원본 없음) |

> 대안: 바이트 대신 **추출 텍스트(plain)** 를 주는 형태도 가능
> (`{ id, title, text }`). 클라이언트는 어느 쪽이든 처리 가능하나, **원본 바이트가 범용적**
> 입니다(클라이언트가 docx/xlsx/pptx/pdf 를 직접 추출 — 로컬 파일과 동일 경로 재사용).

## 클라이언트 사용 흐름 (MoAI Desktop 모드 B)
1. `GET /v1/knowledge?orgId=` 로 문서 목록 브라우즈(이미 동작).
2. 사용자가 문서 선택 → `GET /v1/knowledge/{id}?download=1&orgId=` 로 **원본** 수신.
3. 클라이언트가 텍스트 추출 → 참조로 컨텍스트 주입 → 새 문서 생성.
   → **로컬 파일 첨부와 100% 동일한 경로**가 되어 코드 통일.

## 검증 (배포 후)
```bash
# 200 + 파일 바이트 여야 함
curl -s -D- -o /tmp/doc.bin \
  -H "Authorization: Bearer $MOAI_KEY" \
  "https://vip.bccard.ai/api/v1/knowledge/65?download=1&orgId=<orgId>" | grep -i 'HTTP/\|content-disposition'
file /tmp/doc.bin   # docx/pdf 등으로 인식되어야 함

# 비공개 타인 문서 → 403, 비멤버 org → 403, 없는 문서 → 404
```

## 참고
- 관련 해결 사례: `SERVER_TASK_KNOWLEDGE_SEARCH.md` (동일하게 v1 라우트를 API-키 체인으로 이동).
- 이 작업 전까지 MoAI Desktop 은 조직 문서를 **RAG 스니펫**으로만 참조합니다(원문 통째 첨부 보류).
- 클라이언트 측은 엔드포인트가 열리면 소량 배선(다운로드→추출)만 추가하면 됩니다.
