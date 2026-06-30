# Plan — moai-code ↔ open-moai(vip.bccard.ai) 로그인 연동

> 목표: moai-code 사용자가 **open-moai 계정으로 로그인**하면, moai-code가 open-moai의
> **OpenAI 호환 LLM 엔드포인트**(`https://vip.bccard.ai/api/v1`)를 프로바이더로 사용한다.
> (사용자가 OpenRouter 키를 직접 붙여넣는 대신, open-moai 인증으로 LLM 접근을 받음)

---

## 1. 현재 상태 (조사 결과)

### open-moai (서버, ~/gitHub/open-moai)
- **OpenAI 호환 API**: `app/api/v1/chat/completions/route.ts`, `app/api/v1/models/route.ts`. 표준 OpenAI SSE(`data: {...}` / `data: [DONE]`).
- **인증**: `Authorization: Bearer <api_key>`. 키 형식 `moai-<64 hex>` (SHA256 해시로 `api_keys` 테이블 저장). 검증: `lib/openai-api/middleware.ts` `validateApiKey()`.
- **게이트**(`api_management` 테이블): `apiEnabled`, `openaiCompatible`, `apiKeyEnabled` 가 모두 true여야 함.
- **키 발급**: `POST /api/api-keys` — **admin 전용**(`withAuthRoute({admin:true})`). 평문 키는 생성 시 1회만 반환. **사용자 셀프 발급 UI/엔드포인트 없음.**
- **로그인**: NextAuth v5 Credentials(email/password) → **단기 JWT**(access 15분/prod, refresh 7일), HTTP-only 쿠키. **MFA 있음**(tokenType `mfa-pending`). CLI용 로그인 엔드포인트 없음.
- **JWT를 Bearer로**: `bearerTokenEnabled=true` + `role==admin`일 때만 허용(레이트리밋 무제한). 일반 사용자는 불가.
- 배포: `NEXTAUTH_PUBLIC_URL=https://vip.bccard.ai`, API base `=/api/v1`.

### moai-code (클라이언트)
- `ProviderFactory.CreateDefault`: `OPENAI_API_KEY` + `OPENAI_BASE_URL`(기본 openai) + model(`MOAI_MODEL`/`OPENAI_MODEL`) → `OpenAiChatModel`(+재시도). 키 없으면 EchoModel.
- 설정: `~/.moai/settings.json`(provider/baseUrl/model), 키: `~/.moai/credentials.json`(`OPENAI_API_KEY`, 0600). `FileCredentialStore`.
- `auth` 서브커맨드(set/list). **즉, baseUrl=open-moai, key=moai- 키만 넣으면 이미 동작 가능** — 남은 건 "로그인으로 그 키를 받아오는" 흐름.

---

## 2. 목표 UX (확정) — 설정 제로 로그인

> **B2B 엔터프라이즈 고객 대상이라 "복잡한 설정 금지".** 사용자 경험은 웹사이트 로그인과 동일해야 한다:
>
> 1. `moai` 실행 (또는 미인증 시 자동)
> 2. **아이디(email) / 비밀번호** 입력 — vip.bccard.ai 로그인과 똑같이
> 3. **MFA가 설정돼 있으면 코드 입력**
> 4. **모델 선택**(화살표) — open-moai가 제공하는 모델 목록에서
> 5. 끝. 바로 코딩에 사용
>
> 키 붙여넣기·baseUrl 설정·브라우저 왕복 **전부 없음.** 호스트(vip.bccard.ai)만 빌드/배포 시 기본값으로 박아둔다.

### 왜 CLI 직접 로그인(아이디/비번)인가
- 디바이스/브라우저 플로우(gh-auth 스타일)는 보안상 깔끔하나 **"브라우저 왕복"이 B2B 현장 UX엔 번거롭다.** 사용자는 그냥 앱에서 로그인하길 기대.
- 따라서 **email/password(+MFA)를 moai-code 안에서 직접 입력** → open-moai가 인증하고 **장기 사용자 키를 자동 발급해 반환** → moai-code가 저장. 이후엔 재로그인 불필요(키 만료 전까지).
- 비밀번호는 moai-code가 마스킹 입력으로 받아 **즉시 open-moai로 전송, 저장하지 않음.** 저장하는 건 발급받은 키뿐.

### 핵심 의존성
- open-moai에 **CLI 친화 로그인 + 사용자 키 자동 발급** 엔드포인트가 필요(현재 로그인은 NextAuth 브라우저용, 키 발급은 admin 전용). → 3-A 참조.

---

## 3. 변경 작업

### 3-A. open-moai (서버) — CLI 로그인 + 자동 키 발급

기존 인증 로직(NextAuth Credentials의 `authorize`, MFA, `verifyPassword`, `generateApiKeyMaterial`)을 **재사용**해 CLI용 JSON 엔드포인트만 얇게 추가.

1. **`POST /api/cli/login`** `{ email, password }`
   - 기존 자격 검증 재사용. 결과:
     - MFA 불필요 → `{ status:"ok", apiKey, baseUrl, defaultModel, models:[...] }`
     - MFA 필요 → `{ status:"mfa_required", mfaToken }` (mfaToken = 단기 mfa-pending JWT)
   - 인증 성공 시 **사용자 스코프 API 키를 자동 발급**(아래 공통 발급 로직) 후 평문 1회 반환.
2. **`POST /api/cli/login/mfa`** `{ mfaToken, code }`
   - MFA 코드 검증(기존 로직) → 성공 시 위와 동일한 `{ status:"ok", apiKey, ... }`.
3. **사용자 스코프 키 자동 발급(공통)**
   - `api_keys`에 `user_id=인증된 사용자`, `name="moai-code (CLI)"`, `permissions=["chat","models"]`, **코딩 에이전트용 넉넉한 레이트리밋**, 적정 만료(예: 90일)로 INSERT, 평문 1회 반환.
   - 재로그인 시: 기존 CLI 키 회수 후 재발급(또는 기존 유효 키 재사용). 사용자당 1개 유지.
   - (admin-only `POST /api/api-keys` 와 별개 — 이쪽은 "로그인한 본인" 스코프라 안전.)
4. **레이트리밋 상향**: 코딩 에이전트는 호출이 매우 많음 → CLI 키 tier를 충분히(또는 무제한 tier) 설정.
5. **호스트/모델**: 응답에 `baseUrl`(=배포 호스트의 `/api/v1`)과 `/api/v1/models` 목록을 포함해 CLI가 바로 모델 선택.

> 보안 메모: 비밀번호는 TLS로만 전송, 서버는 저장 안 함. 무차별 대입 방지(레이트리밋/락아웃) 적용. (Phase 0에서 막힌 "DB 직접 삽입" 대신 이 정식 발급 경로를 사용.)

### 3-B. moai-code (클라이언트) — 로그인 화면 + 모델 선택

**첫 실행/미인증 시 자동 로그인 화면** (`moai login` 으로도 호출)
- 호스트는 빌드 기본값(`https://vip.bccard.ai`) — 사용자에게 안 물음(엔터프라이즈 배포).
- 입력 순서(웹 로그인과 동일):
  1. email — LineEditor
  2. password — **마스킹 입력**(에코 없이 `*`; 신규 `ReadPassword` 헬퍼)
  3. `POST /api/cli/login` → `mfa_required`면 MFA 코드 입력 → `POST /api/cli/login/mfa`
  4. 성공 → `apiKey`/`baseUrl` 저장(`credentials.json`/`settings.json`), 비번은 메모리에서 폐기
- **모델 선택**: 응답의 `models`(또는 `/api/v1/models`)로 **SelectList(화살표)** → `settings.json.model` 저장
- 끝 → 평소 REPL 진입. 기존 `OpenAiChatModel`이 baseUrl/key만 open-moai로 향해 그대로 동작.

**부가**
- `moai logout` — 저장 키/세션 제거.
- 기동 헬스체크: 저장 키로 `/api/v1/models` 확인 실패(401/만료) → 자동으로 로그인 화면 재진입(“세션 만료, 다시 로그인”).
- 상태바에 호스트(vip.bccard.ai)·모델 표기, `/login`·`/model`(모델 변경) 슬래시.
- 키 보관: 현재 `credentials.json`(0600) → 추후 OS 키체인.

---

## 4. 단계별 진행 (제안)

- **Phase 0 (연결 검증) — 부분 완료**: vip.bccard.ai 게이트(api_enabled/openai_compatible/api_key_enabled 등) 전부 ON 확인, `/api/v1` 엔드포인트·SSE·키 형식 확인. (기존 `moai-code` 키로 end-to-end는 키 평문 확보 시 즉시 가능)
- **Phase 1 (핵심 UX, MFA 제외)**:
  - open-moai: `POST /api/cli/login` (email/password → 사용자 키 자동 발급 + 모델 목록 반환)
  - moai-code: 첫 실행 로그인 화면(email/마스킹 password) → 모델 SelectList → 저장 → REPL. 호스트 기본값 박기.
  - → "로그인하고 모델 골라 바로 사용" UX 완성(MFA 없는 계정 기준)
- **Phase 2 (MFA)**: open-moai `mfa_required`/`/api/cli/login/mfa` + moai-code MFA 코드 입력 단계. 무차별 대입 방지.
- **Phase 3 (운영 견고화)**: 키 만료/회전, 세션 만료 시 자동 재로그인, `moai logout`, OS 키체인 저장, 레이트리밋 tier 조정, 사용량 표시.

> 빌드 순서상 **open-moai 엔드포인트(서버)가 선행** — moai-code 클라이언트는 그에 맞춰 구현. 병행하려면 클라이언트를 목(mock) 응답으로 먼저 만들고 서버 완성 후 연결.

---

## 5. 주의/리스크
- **MFA**: CLI 직접 로그인(B)은 MFA 코드 입력까지 필요 → 디바이스 플로우(C)가 MFA를 브라우저로 위임해 깔끔.
- **JWT 단기성**: 세션 JWT(15분)는 CLI 장기 사용 부적합 → 반드시 장기 **API 키** 발급해 사용.
- **게이트 토글**: open-moai `api_management`의 apiEnabled/openaiCompatible/apiKeyEnabled가 꺼져 있으면 401 — 운영 기본값 on 필요.
- **키 보관**: 현재 `credentials.json`(0600). 추후 OS 키체인 백엔드로.
- **레이트리밋/권한**: 사용자 키는 chat/models 권한 + 적정 레이트리밋. 코딩 에이전트는 호출이 많으므로 tier 조정 필요할 수 있음.
- **모델 호환**: open-moai가 노출하는 모델이 tool-calling/스트리밍을 지원하는지 확인(코딩 에이전트 품질 직결).
