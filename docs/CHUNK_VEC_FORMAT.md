# `.moai-chunks/` 벡터 포맷 스펙 (외부 임베딩 파이프라인용)

moai-code 의 `ChunkBuild` 가 만든 로컬 텍스트 청크에 **외부 파이프라인이 벡터를 덧붙이는** 계약이다.
moai(CLI)는 벡터를 만들지 않는다 — 파이프라인이 `.vec` 와 `vectors.json` 을 생성하고, CLI 의
`ChunkSearch` 가 그걸 읽어 **오프라인 코사인 top-K** 를 계산한다(질의 벡터는 호출자가 미리 계산).

## 디렉토리 레이아웃

대상 폴더 아래 사이드카 `.moai-chunks/` 안에 공존한다.

```
<공유폴더>/.moai-chunks/
  manifest.json          # moai 소유(텍스트 청크). 파이프라인은 건드리지 않는다.
  vectors.json           # 파이프라인 소유(임베딩 메타). moai 는 읽기만.
  sub/plan.docx.jsonl    # moai 소유: 청크 텍스트, 한 줄당 {"i":N,"text":"..."}
  sub/plan.docx.vec      # 파이프라인 소유: 청크 벡터(아래 포맷)
```

- **소유 분리**: moai 는 `manifest.json` 과 `*.jsonl` 만, 파이프라인은 `vectors.json` 과 `*.vec` 만 쓴다.
  서로의 파일을 덮어쓰지 않는다 → `ChunkBuild` 재실행이 벡터를 지우지 않는다.

## `*.vec` 바이너리 포맷

- **헤더 없음.** 리틀엔디언 `float32` 의 **행 우선(row-major)** 배열 `[청크수 × dim]`.
- i번째 청크의 벡터 = i번째 행. 대응하는 텍스트는 같은 이름 `*.jsonl` 의 `"i":i` 줄.
- 파일 크기 = `청크수 × dim × 4` 바이트여야 한다.
- 벡터는 **정규화 불필요**(ChunkSearch 가 코사인에서 크기를 나눈다). 정규화돼 있어도 무방.

파이썬 예:
```python
import numpy as np
vecs = np.asarray(embeddings, dtype='<f4')   # shape (num_chunks, dim)
with open('.moai-chunks/sub/plan.docx.vec', 'wb') as f:
    f.write(vecs.tobytes())
```

## `vectors.json`

```json
{
  "embModel": "bge-m3",
  "dim": 1024,
  "documents": [
    { "source": "sub/plan.docx", "vec": "sub/plan.docx.vec", "chunks": 12 }
  ]
}
```

| 필드 | 의미 |
|---|---|
| `embModel` | 임베딩 모델 식별자(문자열). 질의도 반드시 **같은 모델**로 임베딩해야 비교가 유효. |
| `dim` | 벡터 차원. 모든 `.vec` 와 질의 벡터가 이 값이어야 한다. |
| `documents[].source` | `manifest.json` 의 `source`(상대경로)와 정확히 일치. |
| `documents[].vec` | `.moai-chunks/` 기준 상대 벡터 파일 경로. |
| `documents[].chunks` | 이 문서의 청크 수. `.jsonl` 줄 수·`.vec` 행 수와 일치해야 한다. |

## 정합성(stale) 규칙

- 문서의 텍스트가 바뀌면 moai 가 `*.jsonl` 을 다시 써서 **청크 수가 달라질 수 있다**.
- `ChunkSearch` 는 `vectors.json` 의 `chunks` 와 현재 `.jsonl` 청크 수가 **다르면 그 문서를 건너뛴다**(stale).
  파이프라인은 원본 변경을 감지해 해당 문서를 재임베딩하고 `vectors.json` 을 갱신해야 한다.
- 권장: 파이프라인이 `manifest.json` 의 `size`/`mtime` 을 참고해 변경 문서만 재임베딩.

## 검색 계약 (참고)

`ChunkSearch` 입력: 대상 폴더 + **질의 벡터**(`dim` 차원, 호출자가 같은 `embModel` 로 미리 계산) + `topK`.
출력: 코사인 유사도 상위 K개 청크(문서·인덱스·점수·텍스트). 질의 임베딩은 CLI 밖에서 수행한다(오프라인 유지).
