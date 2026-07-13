# Plan — moai-code ↔ open-moai(vip.bccard.ai) 로그인 연동

> 목표: moai-code 사용자가 **open-moai 계정으로 로그인**하면, moai-code가 open-moai의
> **OpenAI 호환 LLM 엔드포인트**(`https://vip.bccard.ai/api/v1`)를 프로바이더로 사용한다.
> (사용자가 OpenRouter 키를 직접 붙여넣는 대신, open-moai 인증으로 LLM 접근을 받음)

---

## 1. 현재 상태 (구현 완료)

### open-moai (서버, ~/gitHub/open-moai)
- **CLI 로그인 API 구현**: `POST /api/cli/login` 및 `POST /api/cli/login/mfa` 엔드포인트가 추가되었습니다.
  - **MFA 검증**: `VerifyMfaPendingToken`, TOTP 및 백업코드를 통한 2차 검증을 지원합니다.
  - **사용자 키 발급**: 성공 시 해당 사용자 스코프의 API 키가 자동 생성 및 반환됩니다.
- **OpenAI 호환 API**: `app/api/v1/chat/completions/route.ts`, `app/api/v1/models/route.ts`가 표준 규격을 따릅니다.
- **게이트 관리**: `api_management` 설정을 통해 API 기능 사용 여부가 제어됩니다.

### moai-code (클라이언트)
- **로그인 흐름 탑재**: [LoginFlow.cs](file:///home/jhsoft/gitHub/moai-code/src/MoaiCode.Cli/LoginFlow.cs) 및 [OpenMoaiClient.cs](file:///home/jhsoft/gitHub/moai-code/src/MoaiCode.Cli/OpenMoaiClient.cs)를 통해 CLI 환경에서의 이메일/비밀번호(비밀번호는 마스킹 입력) 및 MFA 코드 검증 흐름이 구현되었습니다.
- **자격 증명 및 설정 자동화**: 로그인 성공 시 `credentials.json`에 `OPENAI_API_KEY`로 자동 등록되며, `settings.json`에 호스트/API 베이스 주소 및 선택한 모델명이 저장됩니다.
- **TUI 자동 연동**: 미인증 상태로 REPL 구동 시, 자동으로 대화형 로그인 인터페이스로 분기합니다.

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
- `/usage`에 계정·조직·호스트·로그인 시각을 표시하고 `/model`에서 모델을 변경한다.
  로그인/로그아웃은 현재 CLI 서브커맨드(`moai login`, `moai logout`)로 제공한다.
- 키 보관: 현재 `credentials.json`(0600) → 추후 OS 키체인.

---

## 4. 단계별 진행 결과 (검증 완료)

- **Phase 0 (연결 검증) — 완료**: open-moai의 API 게이트 상태 체크 및 SSE 규격 호환성 검증이 완료되었습니다.
- **Phase 1 (핵심 UX, MFA 제외) — 완료**:
  - open-moai: `POST /api/cli/login` 구현 완료. (사용자 키 자동 발급 및 모델 목록 반환)
  - moai-code: CLI 로그인 마스킹 입력 인터페이스 및 모델 `SelectList` UI 구성 완료.
- **Phase 2 (MFA 대응) — 완료**:
  - open-moai: `POST /api/cli/login/mfa` 및 TOTP/백업코드 검증 로직 구현 완료.
  - moai-code: 1차 로그인 응답 결과 `mfa_required` 시 MFA 입력 단계를 동적으로 연결하여 수행하도록 구현 완료.
- **Phase 3 (운영 견고화) — 핵심 완료**:
  - `moai logout`을 통한 자격 증명 제거 및 관련 설정 클리어 기능 제공.
  - 자격 증명 부재 시 대화형 REPL에서 자동 로그인하는 흐름 구축 완료.
  - 저장 키의 서버 유효성을 기동 시 선검증해 만료 키를 자동 재로그인시키는 헬스체크는 후속 과제.
  - (추후 과제) OS 키체인 백엔드 스토어 적용 검토.

> 빌드 순서상 **open-moai 엔드포인트(서버)가 선행** — moai-code 클라이언트는 그에 맞춰 구현. 병행하려면 클라이언트를 목(mock) 응답으로 먼저 만들고 서버 완성 후 연결.

---

## 5. 주의/리스크
- **MFA**: CLI 직접 로그인(B)은 MFA 코드 입력까지 필요 → 디바이스 플로우(C)가 MFA를 브라우저로 위임해 깔끔.
- **JWT 단기성**: 세션 JWT(15분)는 CLI 장기 사용 부적합 → 반드시 장기 **API 키** 발급해 사용.
- **게이트 토글**: open-moai `api_management`의 apiEnabled/openaiCompatible/apiKeyEnabled가 꺼져 있으면 401 — 운영 기본값 on 필요.
- **키 보관**: 현재 `credentials.json`(0600). 추후 OS 키체인 백엔드로.
- **레이트리밋/권한**: 사용자 키는 chat/models 권한 + 적정 레이트리밋. 코딩 에이전트는 호출이 많으므로 tier 조정 필요할 수 있음.
- **모델 호환**: open-moai가 노출하는 모델이 tool-calling/스트리밍을 지원하는지 확인(코딩 에이전트 품질 직결).
