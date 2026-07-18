# moai-code 로컬 청킹 기능 — 서버팀 연동 가이드

> 대상: open-moai / 임베딩 파이프라인 담당. 목적: moai-code(CLI)가 만드는 로컬 청크 구조를 이해하고,
> **외부 임베딩 파이프라인이 붙일 부분(`.vec` + `vectors.json`)의 계약**을 정확히 맞추기 위함.
> 관련 스펙: [`CHUNK_VEC_FORMAT.md`](./CHUNK_VEC_FORMAT.md) (벡터 파일 포맷의 최종 근거).

---

## 1. 개요

기업 사용자의 **PC / 회사 공유 폴더**에 있는 문서를 미리 청킹해두고, moai-code CLI로 그 청크(및
외부에서 붙인 벡터)를 가져가는 **완전 로컬·오프라인** 파이프라인이다. 서버(open-moai)의 문서함/RAG와는
독립적이며, 네트워크 없이 동작한다.

역할 분담이 핵심이다:

| 주체 | 하는 일 | 소유 파일 |
|---|---|---|
| **moai-code (CLI)** | 문서 텍스트 추출 → 청킹 → 로컬 저장, 청크 가져오기, 벡터 코사인 검색(계산만) | `manifest.json`, `*.jsonl` |
| **임베딩 파이프라인 (서버팀)** | 청크 텍스트를 임베딩 → 벡터 파일 생성, 정합성 유지 | `vectors.json`, `*.vec` |

moai-code 는 **임베딩을 하지 않는다**(self-contained 바이너리에 모델 없음). 서버팀 파이프라인이
벡터를 생성해 사이드카에 넣으면, moai-code 의 `ChunkSearch` 가 그걸 읽어 **오프라인 코사인 top-K** 를
계산한다. 질의(query) 임베딩도 CLI 밖에서 같은 모델로 계산해 넘겨줘야 한다.

---

## 2. 데이터 흐름

```
공유폴더/문서들 ─(ChunkBuild)→ .moai-chunks/manifest.json + *.jsonl   [moai 소유: 텍스트 청크]
                                     │
              (임베딩 파이프라인)     ▼
                          .moai-chunks/vectors.json + *.vec           [서버팀 소유: 벡터]
                                     │
   질의텍스트 ─(파이프라인 임베딩)→ queryVector ─(ChunkSearch)→ 코사인 top-K 청크
```

- moai 의 `ChunkBuild` 가 먼저 돌아 텍스트 청크를 만든다.
- 파이프라인이 그 청크(`*.jsonl`)를 읽어 임베딩하고 `*.vec` + `vectors.json` 을 같은 폴더에 쓴다.
- 검색 시 파이프라인(또는 호출자)이 **질의 텍스트를 같은 모델로 임베딩**해 `ChunkSearch` 에 벡터로 전달.

---

## 3. 사이드카 레이아웃 `.moai-chunks/`

대상 폴더(또는 파일의 부모) 아래에 사이드카가 생긴다. **원본 문서는 절대 건드리지 않는다.**

```
<공유폴더>/.moai-chunks/
  manifest.json          # moai 소유 — 청킹 메타(증분 판단 포함)
  vectors.json           # 서버팀 소유 — 임베딩 메타
  report.docx.jsonl      # moai 소유 — 청크 텍스트
  report.docx.vec        # 서버팀 소유 — 청크 벡터
  sub/plan.xlsx.jsonl    # 하위폴더 구조를 미러링(충돌 방지)
  sub/plan.xlsx.vec
```

**소유 분리 원칙**: moai 는 `manifest.json`/`*.jsonl` 만, 파이프라인은 `vectors.json`/`*.vec` 만 쓴다.
서로의 파일을 덮어쓰지 않으므로 `ChunkBuild` 재실행이 벡터를 지우지 않는다.

- 문서별 파일명은 **앵커 기준 상대경로를 미러링**한다: `sub/plan.xlsx` → `.moai-chunks/sub/plan.xlsx.jsonl`.
- 앵커: `ChunkBuild path` 가 디렉토리면 그 디렉토리, 파일이면 그 파일의 부모.

---

## 4. moai 가 만드는 파일

### 4.1 `manifest.json` (moai 소유)

```json
{
  "chunkSize": 1000,
  "overlap": 150,
  "documents": [
    { "source": "report.docx", "size": 62220, "mtime": 638880000000000000, "chunks": 12, "file": "report.docx.jsonl" }
  ]
}
```

| 필드 | 의미 |
|---|---|
| `chunkSize` / `overlap` | 청킹 파라미터(문자 단위). 바뀌면 전체 재청킹. |
| `documents[].source` | 앵커 기준 상대경로(파이프라인이 `vectors.json` 에서 이 값을 그대로 참조). |
| `documents[].size` / `mtime` | 원본 파일 크기·최종수정시각(UTC ticks). **증분 판단·정합성 참고용.** |
| `documents[].chunks` | 청크 수(= `.jsonl` 줄 수 = `.vec` 행 수여야 함). |
| `documents[].file` | 청크 텍스트 파일(상대). |

### 4.2 `*.jsonl` (moai 소유) — 청크 텍스트

한 줄에 청크 하나. 순서 = 인덱스.

```
{"i":0,"text":"첫 번째 청크 본문 ..."}
{"i":1,"text":"두 번째 청크 본문 ..."}
```

---

## 5. 서버팀이 만들 파일 (임베딩 파이프라인 계약)

> 최종 근거는 [`CHUNK_VEC_FORMAT.md`](./CHUNK_VEC_FORMAT.md). 아래는 요약.

### 5.1 `*.vec` — 청크 벡터

- **헤더 없음.** 리틀엔디언 `float32` 의 **행 우선** 배열 `[청크수 × dim]`.
- i번째 행 = `*.jsonl` 의 `"i":i` 청크 벡터. 파일 크기 = `청크수 × dim × 4` 바이트.
- 정규화 불필요(ChunkSearch 가 코사인에서 크기로 나눔).

```python
import numpy as np, json
chunks = [json.loads(l) for l in open('.moai-chunks/report.docx.jsonl', encoding='utf-8')]
vecs = embed([c["text"] for c in chunks])          # shape (N, dim), 같은 모델 고정
np.asarray(vecs, dtype='<f4').tofile('.moai-chunks/report.docx.vec')
```

### 5.2 `vectors.json`

```json
{
  "embModel": "bge-m3",
  "dim": 1024,
  "documents": [
    { "source": "report.docx", "vec": "report.docx.vec", "chunks": 12 }
  ]
}
```

- `embModel`: 임베딩 모델 식별자. **질의도 반드시 같은 모델**로 임베딩해야 비교가 유효.
- `dim`: 벡터 차원. 모든 `.vec`·질의 벡터가 이 값.
- `documents[].source`: `manifest.json` 의 `source` 와 **정확히 일치**.
- `documents[].vec`: `.moai-chunks/` 기준 상대 경로.
- `documents[].chunks`: 청크 수. `.jsonl` 줄 수·`.vec` 행 수와 일치해야 함.

---

## 6. 지원 문서 형식 (텍스트 추출)

| 형식 | 추출 방식 | 비고 |
|---|---|---|
| `.txt` `.md` `.csv` | 원문 그대로 | — |
| `.docx` | Open XML(문단 텍스트) | — |
| `.xlsx` | Open XML(셀, 공유문자열 해소; 셀=탭, 행=줄바꿈) | — |
| `.pptx` | Open XML(슬라이드 텍스트) | — |
| `.pdf` | PdfPig(읽기순 재구성) | **스캔 PDF(이미지)는 텍스트 없음 → 빈 추출** |

지원 외 확장자는 **디렉토리 청킹 시 자동 제외**된다.

---

## 7. 청킹 알고리즘

- 기본 **청크 1000자 · 오버랩 150자**(둘 다 조절 가능).
- 개행 정규화(연속 빈 줄은 최대 2개까지 유지).
- 슬라이딩 윈도우로 최대 크기까지 자르되, **단어 절단을 피해 마지막 공백에서 끊는다**.
- 각 청크 길이 ≤ `chunkSize` 보장, 인접 청크는 `overlap` 만큼 겹침.

---

## 8. 증분(incremental) 규칙 — 서버팀 참고

- `ChunkBuild` 는 `manifest.json` 의 `size`+`mtime` 이 **그대로면 그 문서를 다시 청킹하지 않는다**(스킵).
- `chunkSize`/`overlap` 이 바뀌거나 `force:true` 면 전체 재청킹.
- **파이프라인 권장 로직**: `manifest.json` 을 읽어 `size`/`mtime`(또는 `chunks`)이 자신이 마지막으로
  임베딩한 값과 다른 문서만 재임베딩하면 비용 최소화.

---

## 9. 정합성(stale) 처리 — 중요

원본 문서가 바뀌면 moai 가 `*.jsonl` 을 다시 써서 **청크 수가 달라질 수 있다**. 이때 이전 `*.vec` 는
행 수가 안 맞는 낡은(stale) 벡터가 된다.

- `ChunkSearch` 는 `vectors.json` 의 `chunks` 와 **현재 `.jsonl` 청크 수가 다르면 그 문서를 건너뛴다**
  (검색 결과에 `stale N개 건너뜀` 으로 표시). 즉 **잘못된 매칭을 내지 않는다.**
- 따라서 파이프라인은 원본/청크 변경을 감지해 **해당 문서를 재임베딩하고 `vectors.json` 을 갱신**해야
  한다. `manifest.json` 의 `size`/`mtime`/`chunks` 가 감지 신호.

---

## 10. moai CLI 툴 3종 (참고)

| 툴 | 종류 | 역할 |
|---|---|---|
| `ChunkBuild` | 쓰기 | 파일/폴더 청킹 → `.moai-chunks/` 저장. `recursive`, `chunkSize`, `overlap`, `force`. |
| `ChunkFetch` | 읽기 | 디렉토리→개요, 파일/`source`→청크 순서대로(랭킹 없음). `offset`/`limit`. |
| `ChunkSearch` | 읽기 | `queryVector`(또는 `queryVectorFile`) → 코사인 top-K. `topK`, `source` 필터. |

- `ChunkSearch` 는 **오프라인 계산만** 한다. 질의 벡터는 호출자(파이프라인/서버)가 같은 `embModel` 로
  미리 계산해 넘긴다(인라인 배열 또는 float32 `.vec` 단일행 파일).
- 청크·검색 결과의 **본문은 신뢰불가 경계(`<system-reminder>`)로 감싸** LLM 프롬프트 인젝션을 방어한다.

---

## 11. E2E 예시

```
# 1) (CLI) 공유폴더 청킹
ChunkBuild  path="/mnt/share/규정"  recursive=true
   → .moai-chunks/manifest.json + *.jsonl 생성

# 2) (파이프라인) 각 *.jsonl 읽어 임베딩 → *.vec + vectors.json 작성
#    - manifest 의 source 를 그대로 사용, chunks 수 일치, dim/embModel 고정

# 3) (파이프라인/서버) 질의 "연차 규정" 을 같은 모델로 임베딩 → queryVector
# 4) (CLI) 검색
ChunkSearch path="/mnt/share/규정"  queryVector=[...]  topK=5
   → 코사인 상위 5개 청크(문서·인덱스·점수·텍스트)
```

---

## 12. 제약·주의

- **오프라인 저장, 온라인 임베딩**: 벡터 생성·질의 임베딩은 파이프라인 몫. CLI 는 저장·계산만.
- **모델 일치 필수**: 저장 벡터와 질의 벡터의 `embModel`/`dim` 이 다르면 결과 무의미(dim 다르면 CLI 가 거부).
- **스캔 PDF**: 이미지 기반 PDF 는 텍스트가 없어 청크가 비거나 적을 수 있음(OCR 미포함).
- **소유 분리 준수**: 파이프라인은 `vectors.json`/`*.vec` 만 쓸 것. `manifest.json`/`*.jsonl` 은 moai 소유.
- **정합성**: 원본 변경 시 재임베딩 + `vectors.json` 갱신 필요(안 하면 해당 문서는 stale 로 검색 제외).
