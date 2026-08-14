# [서버 이슈] `/api/v1/search` 웹검색이 매번 0건 반환 (SearXNG 빈 결과)

> **상태: 진단(2026-08-14). moai-code(MoAI Desktop) 측 실서버 트레이스로 확인.** 서버/인프라 조치 필요.
> 대상: open-moai `app/api/v1/search/route.ts`, `lib/websearch/services/searxng.ts`, SearXNG 인스턴스.

## 증상
MoAI Desktop 에서 문서 생성 시 모델이 `WebSearch` 를 여러 번 호출하는데 **매 호출이 결과 0건**이다.
UDP 실시간 트레이스(2026-08-14 실서버 vip.bccard.ai):
```
TOOL-IN  WebSearch | {"query":"AI governance official framework ...","limit":10}
TOOL-OUT WebSearch err=False (12) | (no results)      ← 12자 = 클라의 "(no results)"
... (site:nist.gov 등 여러 쿼리 전부 동일)
```
- HTTP 에러 아님(클라 `error=False`) → 서버가 **200 `{results:[], count:0}`** 반환.
- `OrgDocs`(문서함 검색)는 정상 동작(실결과 1980~2814자). 즉 인증·네트워크·API키는 정상.
- 부작용: 모델이 웹 근거를 못 구해 **여러 쿼리로 턴·토큰 낭비**, 결과물에 "웹 검색 결과 미회신" 명기 → 웹 근거 0.

## 원인 사슬 (코드 확인)
1. `route.ts`: `settings.enabled=true` + 엔진구성 존재 → 400("not configured") 안 뜸.
2. `route.ts`: `executeMultiEngineSearch` 가 예외 없이 `results:[]` 반환.
3. `route.ts`의 503("engines unavailable")는 **`!anyFulfilled && summaries.length>0`** 일 때만. 지금은
   엔진이 `fulfilled`(성공)했으나 count=0 이라 이 경로 안 탐 → **200 빈 결과로 폴백**.
4. `searxng.ts`: **"engines unavailable" throw 조건이 `filteredItems===0 && data.unresponsive_engines.length>0`**.
   SearXNG 가 `{results:[], unresponsive_engines:[]}`(빈 결과 + 미보고)를 주면 **정상 0건으로 간주** → 빈 결과 반환.

→ 결론: **SearXNG 인스턴스가 200 + 빈 `results` + 빈 `unresponsive_engines` 를 반환**하고 있다.
   = SearXNG 에 **동작하는 상위 엔진이 없음**(전부 비활성/차단), 또는 **엔드포인트·파라미터·인증 misconfig**.

## 서버팀 점검 항목
1. **SearXNG 직접 호출 테스트**: `curl "{SEARXNG_URL}/search?q=AI+governance&format=json"` →
   `results` 가 실제로 채워지는지, `unresponsive_engines` 에 뭐가 뜨는지 확인.
2. **SearXNG 엔진 설정**: 활성 엔진이 하나라도 있는지(google/duckduckgo 등). 전부 rate-limit/차단이면 빈 결과.
3. **websearch-settings**(DB): `enabled`, 선택된 engine, 해당 엔진 `enabled !== false`, URL/API키 유효성.
4. **3초 타임아웃**(`searxng.ts` makeRequest 3000ms): SearXNG 응답이 느리면 rejected 지만, 지금은 빈 결과라 별개.
5. `buildUrl` 파라미터: `format=json`, `categories`/`engines`/`language` 등 SearXNG 가 요구하는 필수 파라미터 누락 여부.

## 서버측 개선 제안(선택)
- 빈 결과 + `unresponsive_engines` 도 비었지만 **활성 엔진이 0개**면, "정상 0건"이 아니라 **misconfig** 로
  구분해 503/경고 반환(현재는 조용히 200 empty → 진짜 고장이 "결과 없음"으로 은폐됨).
- `/api/v1/search` 응답에 `engines`(각 엔진 status)를 항상 포함하면 클라/디버깅에서 원인 즉시 파악 가능.

## 클라(moai-code) 측
- 클라는 정상. 서버가 200 empty 를 주면 `(no results)` 로 표기할 뿐. (모델이 반복 호출로 낭비하는 것도
  근본은 서버가 빈 결과를 주기 때문.) 서버 수정 후 재검증 예정.
