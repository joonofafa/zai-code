# [서버 버그] record-collections/query — 모든 SELECT가 "read-only SELECT queries only"로 거부됨

- **심각도**: High (기능 완전 불통)
- **영향 API**: `POST /api/v1/record-collections/query` (moai-code `OrgDatas` 툴)
- **대상 코드**: open-moai `lib/knowledge/record-db-service.ts`, `lib/security/sql-injection-detector.ts`
- **브랜치**: `dev-ext-pending` 기준 확인
- **진단**: moai-code(CLI) 측, 실서버 vip.bccard.ai E2E 중 발견

---

## 1. 증상

`OrgDatas` 로 데이터함 SQL 조회 시 **모든 SELECT 계열 쿼리가 400으로 거부**된다. 실제 세션 로그:

```
SELECT 브랜드명, SUM(총이용금액) ... FROM cache_cafe_month_brand ...
  → HTTP 400 {"error":"read-only SELECT queries only"}
SELECT * FROM cache_cafe_month_brand LIMIT 1
  → HTTP 400 {"error":"read-only SELECT queries only"}
DESCRIBE cache_cafe_month_brand
  → HTTP 400 {"error":"read-only SELECT queries only"}
SELECT 1
  → HTTP 400 {"error":"read-only SELECT queries only"}   ← 가장 단순한 쿼리조차 거부
```

- `OrgDatasList`(컬렉션/스키마 목록)는 정상(133줄 반환) → 인증·라우팅·컬렉션 접근은 OK.
- 존재하지 않는 collection_id 는 별도로 403(`collection not found or not permitted`) 반환 →
  즉 `cache_cafe_month_brand` 는 접근 가능한 컬렉션이며, **문제는 순수하게 SQL 검증 단계**다.

---

## 2. 근본 원인 (확정) — 속성명 불일치 `.safe` vs `.isSafe`

`lib/knowledge/record-db-service.ts` L96–103, `guardReadOnlySql`:

```ts
const inj = detectSqlInjection(sql)              // 반환 타입: { isSafe: boolean, ... }
const kw  = checkDangerousKeywords(sql, false)   // 반환 타입: { isSafe: boolean, ... }
if (!inj.safe || !kw.safe) {                     // ❌ .safe 를 읽음 — 실제 필드는 .isSafe
  return { ok: false, status: 400, error: 'read-only SELECT queries only' }
}
```

`detectSqlInjection`/`checkDangerousKeywords` 는 `SqlInjectionCheckResult`
(`lib/security/sql-injection-detector.ts` L6–7: `isSafe: boolean`)를 반환하는데, 가드는
**존재하지 않는 필드 `.safe`** 를 읽는다.

- `inj.safe` = `undefined`, `kw.safe` = `undefined`
- `!undefined` = `true` → `if (true || true)` → **입력과 무관하게 항상 거부**

그래서 `SELECT 1` 을 포함한 모든 쿼리가 400 이 된다. 반대로, 이 검증이 **실제로는 전혀 동작하지
않으므로**(항상 reject 라 통과 경로가 없음) 위험 쿼리 차단 여부도 검증된 적이 없다.

### 수정 (한 줄)

```ts
// L102
if (!inj.isSafe || !kw.isSafe) {
```

---

## 3. 2차 버그 (같이 고치기 권장) — 정규식 `g` 플래그 상태성

`lib/security/sql-injection-detector.ts` 의 `CRITICAL_PATTERNS`/`HIGH_PATTERNS`/`MEDIUM_PATTERNS`
는 **모듈 레벨 상수 + `g`/`gm` 플래그**(예: L33 `/;\s*(DROP|DELETE|TRUNCATE|ALTER)\b/gi`,
L19~ `/gi`)인데, `detectSqlInjection` 이 이를 `pattern.test(query)` 로 재사용한다.

- `g` 플래그가 붙은 정규식의 `.test()` 는 **`lastIndex` 를 유지**한다(stateful). 서버는 단일
  프로세스에서 여러 요청을 처리하므로, 앞선 쿼리에서 매치된 패턴의 `lastIndex` 가 남아 다음
  쿼리에서 **매치를 건너뛰는 false negative** 가 발생할 수 있다(위험 쿼리 누락 = 보안 리스크).
- 1차 버그(항상 reject) 때문에 지금은 드러나지 않지만, 1차를 고쳐 검증이 실제로 동작하기
  시작하면 간헐적 오탐/누락으로 나타난다.

### 수정 옵션 (택1)

- 각 `.test()` 전에 `pattern.lastIndex = 0` 리셋, **또는**
- 존재성 판정에는 `g` 플래그 제거(매칭 위치가 필요 없으므로 `/.../i` 로 충분), **또는**
- 루프마다 `new RegExp(pattern.source, 'i')` 로 새 인스턴스 사용.

---

## 4. 검증 시나리오 (수정 후)

**통과해야 함(200):**
```sql
SELECT 1
SELECT * FROM cache_cafe_month_brand LIMIT 10
SELECT 브랜드명, SUM(총이용금액) AS 총매출 FROM cache_cafe_month_brand GROUP BY 브랜드명 ORDER BY 총매출 DESC LIMIT 10
WITH t AS (SELECT * FROM cache_cafe_month_brand) SELECT * FROM t LIMIT 5
```

**차단해야 함(400/403):**
```sql
DROP TABLE cache_cafe_month_brand
DELETE FROM cache_cafe_month_brand
SELECT * FROM cache_cafe_month_brand; DROP TABLE x        -- 다중문
SELECT * FROM other_org_table                              -- 403(테이블 격리)
SELECT * FROM cache_cafe_month_brand UNION SELECT ...      -- UNION 주입
```

- 특히 `g` 플래그 수정 후, **위 차단 케이스를 연속으로 여러 번 반복 호출**해도 매번 차단되는지
  확인(상태성 회귀 방지).

---

## 5. 참고

- moai-code 클라이언트(`OrgDatas`)는 SQL 을 가공 없이 그대로 전송하며, 자체적으로도 첫 키워드가
  SELECT/WITH/EXPLAIN/SHOW/DESCRIBE/PRAGMA 인지 클라이언트 가드를 통과한 쿼리만 보낸다.
  즉 서버가 받은 SQL 은 정상적인 읽기 쿼리이고, 거부는 100% 서버 검증 로직 문제다.
- 관련 작업지시서: [`SERVER_TASK_RECORD_DATA.md`](./SERVER_TASK_RECORD_DATA.md)
