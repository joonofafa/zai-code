# 서버 회신: 로컬 청킹 파이프라인 임베딩 API (`/api/v1/embeddings`)

> 대상: moai-code 로컬 청킹 파이프라인 담당.
> 근거 문서: [`LOCAL_CHUNKING.md`](./LOCAL_CHUNKING.md) §5(파이프라인 계약), [`CHUNK_VEC_FORMAT.md`](./CHUNK_VEC_FORMAT.md).
> 상태: **서버 구현 완료 (2026-07-18, vip.bccard.ai 배포).** 아래는 확정 계약.

---

## 1. 결정 사항 (서버측 확정)

| 항목 | 결정 | 이유 |
|---|---|---|
| **오프라인 요건** | 청킹·검색은 로컬, **임베딩은 온라인(vip API 호출)** | 문서 §12 가 이미 인정한 모델. 진짜 에어갭은 아님 |
| **임베딩 모델** | **`text-embedding-3-small` (dim 1536)** | vip 운영 표준 모델. 추가 인프라 0 |
| **파이프라인 구조** | 파이프라인이 vip `/api/v1/embeddings` 를 Bearer 로 호출 | 문서/record/이미지 API 와 동일 패턴 |

> ⚠️ **`LOCAL_CHUNKING.md`/`CHUNK_VEC_FORMAT.md` 의 예시값(`bge-m3`, dim 1024)은 예시일 뿐**이다.
> vip 표준은 **`text-embedding-3-small`, dim 1536**. `vectors.json` 의 `embModel`/`dim` 은 아래 API
> 응답의 `model`/`dimensions` 를 그대로 기록할 것.

---

## 2. 엔드포인트: `POST /api/v1/embeddings`

OpenAI `/v1/embeddings` 호환. Bearer API 키 인증(세션 없음).

```jsonc
POST https://vip.bccard.ai/api/v1/embeddings
Authorization: Bearer moai-<...>
Content-Type: application/json
{
  "input": ["첫 번째 청크 텍스트", "두 번째 청크 텍스트", "..."],   // string 또는 string[]
  "model": "text-embedding-3-small"                                  // 선택. 생략 시 이 값이 기본
}
```

응답(200, OpenAI 호환):
```jsonc
{
  "object": "list",
  "data": [
    { "object": "embedding", "index": 0, "embedding": [0.0123, -0.045, ...] },  // 1536개 float
    { "object": "embedding", "index": 1, "embedding": [ ... ] }
  ],
  "model": "text-embedding-3-small",
  "dimensions": 1536,
  "usage": { "prompt_tokens": 0, "total_tokens": 0 }   // provider 가 계량 미노출 → 0 고정
}
```

- **`data` 는 입력 순서 보존**. `data[i].embedding` = `input[i]` 의 벡터.
- 한 요청당 **최대 512개** 입력(초과 시 400). 큰 문서는 청크를 나눠 여러 번 호출.
- `model` 은 vip 에 등록된 임베딩 모델명만 허용(미등록 → 400). 현재 사용 가능:
  `text-embedding-3-small`(권장), `text-embedding-3-large`, `text-embedding-ada-002`,
  `gemini-embedding-001` 등. **저장 벡터와 질의 벡터는 반드시 같은 model 로.**

### 2.1 오류
| 상황 | 코드 | 응답 |
|---|---|---|
| `input` 누락/빈 배열 | 400 | `{ "error": "input is required (string or string[])" }` |
| `input` 에 비문자열 | 400 | `{ "error": "input must contain only strings" }` |
| 입력 512개 초과 | 400 | `{ "error": "too many inputs (max 512)" }` |
| `model` 미등록 | 400 | `{ "error": "embedding model '<x>' not found" }` |
| 무효 API 키 | 401 | `{ "error": { "message": "Invalid API key provided", ... } }` |
| 임베딩 실패(상류 오류) | 502 | `{ "error": "..." }` |

---

## 3. 파이프라인 연동 절차 (LOCAL_CHUNKING §5 매핑)

1. CLI `ChunkBuild` → `.moai-chunks/manifest.json` + `*.jsonl` 생성(로컬).
2. 파이프라인이 각 `*.jsonl` 을 읽어 `text` 배열을 만든다.
3. **512개씩 잘라** `POST /api/v1/embeddings` 호출 → `data[].embedding` 수집(순서 유지).
4. 수집한 벡터를 **리틀엔디언 float32 행우선**으로 `*.vec` 에 기록
   (`CHUNK_VEC_FORMAT.md` §`*.vec`). 행 수 = `.jsonl` 줄 수.
5. `vectors.json` 작성:
   ```json
   {
     "embModel": "text-embedding-3-small",   // ← 응답 model 그대로
     "dim": 1536,                            // ← 응답 dimensions 그대로
     "documents": [ { "source": "report.docx", "vec": "report.docx.vec", "chunks": 12 } ]
   }
   ```
6. **질의 임베딩**도 같은 엔드포인트로: `input:["질의문"]` → `data[0].embedding` 을 CLI `ChunkSearch`
   의 `queryVector` 로 전달.

파이썬 예:
```python
import requests, numpy as np, json

BASE = "https://vip.bccard.ai/api/v1"
H = {"Authorization": "Bearer moai-...", "Content-Type": "application/json"}

texts = [json.loads(l)["text"] for l in open(".moai-chunks/report.docx.jsonl", encoding="utf-8")]

vecs = []
for i in range(0, len(texts), 512):
    r = requests.post(f"{BASE}/embeddings",
                      headers=H, json={"input": texts[i:i+512], "model": "text-embedding-3-small"})
    r.raise_for_status()
    body = r.json()
    vecs += [d["embedding"] for d in body["data"]]   # data 는 순서 보존

np.asarray(vecs, dtype="<f4").tofile(".moai-chunks/report.docx.vec")
json.dump({"embModel": body["model"], "dim": body["dimensions"],
           "documents": [{"source": "report.docx", "vec": "report.docx.vec", "chunks": len(texts)}]},
          open(".moai-chunks/vectors.json", "w", encoding="utf-8"), ensure_ascii=False)
```

---

## 4. 주의 (서버가 짚는 정합성 갭)

- **모델 일치 필수**: 저장 벡터와 질의 벡터의 `model` 이 다르면 검색 무의미. 항상 `text-embedding-3-small`
  로 통일하거나, 바꿀 거면 전 문서 재임베딩 + `vectors.json.embModel` 갱신.
- **`source` 문자열 정확 일치**(LOCAL_CHUNKING §5.2): `vectors.json.documents[].source` 는
  `manifest.json.documents[].source` 와 바이트 단위로 같아야 한다. 크로스 플랫폼에서 경로 구분자(`\`↔`/`)·
  유니코드 정규화(NFC/NFD) 가 어긋나지 않게 **manifest 값을 그대로 복사**할 것.
- **증분**(LOCAL_CHUNKING §8): `manifest.json` 의 `size`/`mtime`(.NET UTC ticks = 1e-7초 단위)이
  마지막 임베딩 때와 다른 문서만 재호출하면 비용 절감. ticks→epoch 변환이 필요하면
  `epoch_seconds = ticks/1e7 - 62135596800`.
- **rate limit**: Bearer 키에 레이트리밋이 걸릴 수 있다(대량 문서 시 429). 429 면 backoff 후 재시도.

---

## 5. 서버 구현 위치 (참고)

| 항목 | 파일 |
|---|---|
| 라우트 | `app/api/v1/embeddings/route.ts` |
| 임베딩 서비스 | `lib/embeddings/v1-embedding-service.ts` (운영 문서 임베딩과 동일한 `EmbeddingProviderFactory` 경로) |
| 권한/인증 | `endpoint-policy.ts`(`/v1/embeddings`=chat) + `proxy.ts`(csrfExempt·publicPaths) + `v1-guard.ts` |
