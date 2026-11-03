# Bundled skill sources

이 폴더는 **MATE Builder가 자체 저작한 기본 번들 스킬의 원본(SKILL.md)**입니다. 빌드 시 실제로 배포되는 것은
같은 디렉터리의 `bundled-skills.zip`(임베디드 리소스)이며, 이 폴더는 그 zip 을 다시 만들 수 있게 소스를
추적하기 위한 것입니다.

## 현재 자체 저작 스킬
- `xunit-test-writer` — 변경 코드 xUnit 테스트 작성/보강
- `business-report` — 데이터/조직문서 → 네이티브 Docx/XlsxCreate 보고서 생성
- `code-review` — 정확성·보안·호환성·성능·테스트 관점의 변경사항 검토 (v5)
- `systematic-debugging` — 재현→원인 분리→근본 원인→최소 수정→검증 절차 (v5)
- `source-driven-development` — 낯선 API는 설치된 소스·공식 문서로 검증 후 작성 (v5)
- `skill-creator` — 스킬 작성·개선·평가 루프, MATE Builder 스킬 메커니즘 기준 (v9)
- `cross-layer-change-review` — 커밋/PR/범위의 계층 간 배선 누락 중심 교차 검토 (v10, 사용자 로컬 스킬을 자체 저작 재집필 후 등재)

### 사내 인프라·오프라인 스킬 (v12, 2026-11-03)
망분리 사내망(외부 연결 없음)에서 인프라·코딩 에이전트로 쓰기 위한 9종. 모두 자체 저작(외부 원문 없음)이고,
본문에 인터넷 주소·다운로드 지시가 없다(`BundledSkillsTests` 가 검사). 참고표는 각 스킬의 `references/`, 보조 스크립트는
`scripts/` 에 있고, Skill 툴이 본문 앞에 스킬 폴더 경로를 붙여 모델이 찾아 읽는다.
- `man-page-lookup` — man·--help·info·/usr/share/doc 등 설치된 문서로 명령·설정 확인
- `oracle-to-postgresql` — Oracle SQL·DDL·PL/SQL → PostgreSQL(eXperDB 등) 변환과 결과 동일성 검증
- `postgresql-diagnostics` — 세션·잠금·연결·복제·vacuum·용량 진단(읽기 전용 쿼리 모음)
- `openshift-diagnostics` — oc/kubectl 로 파드 상태별 원인 좁히기, SCC·Route·GPU 함정
- `k8s-manifest-review` — 배포 YAML 검토(자원·probe·자동 확장·장애 격리·보안·내부 레지스트리)
- `vllm-serving` — vLLM 서빙 크기 계산·플래그·오프라인 적재·OpenShift AI·에이전트용 tool 호출 설정
- `financial-secure-code-review` — 카드번호·CVV·주민번호·자격증명·로그 노출·MyBatis `${}`·TLS·외부 전송 점검(`scripts/find_pan.py` Luhn 검사, MIT)
- `external-code-intake` — 외부망에서 들여오는 코드·패키지·스킬 반입 검토(라이선스는 MIT/Apache 만 기본 수용)
- `change-and-incident-runbook` — 변경 계획(사전 점검·단계·중단 조건·롤백)과 장애 대응·보고서 양식

> 오프라인 모드(`settings.json` 의 `"offline": true` 또는 `MOAI_OFFLINE=1`)에서는 `mcp-builder` 가 지시하는 웹 문서 가져오기가 동작하지 않는다.

> `cross-layer-change-review`는 `~/.claude/skills/`에 동일 이름의 사용자 로컬 원본(한글)이 있어
> 로컬 쪽이 우선 적용된다(user > bundled). 번들본은 영문 자체 저작 스냅숏이며, 로컬 원본을
> 유지하는 동안 번들본 수정은 이 머신에서는 즉시 반영되지 않는다.

> - `code-review` — 원칙 참조: [getsentry/skills](https://github.com/getsentry/skills) code-review (Apache-2.0) — 본문 자체 저작, 원문 미포함
> - `systematic-debugging` — 원칙 참조: [obra/superpowers](https://github.com/obra/superpowers) systematic-debugging (MIT) — 본문 자체 저작, 원문 미포함
> - `source-driven-development` — 원칙 참조: [addyosmani/agent-skills](https://github.com/addyosmani/agent-skills) source-driven-development (MIT) — 본문 자체 저작, 원문 미포함
> - `skill-creator` — 원칙 참조: [anthropics/skills](https://github.com/anthropics/skills) skill-creator (Apache-2.0) — 본문 자체 저작, 원문 미포함
> - `xunit-test-writer`, `cross-layer-change-review` — 외부 참조 없는 순수 자체 저작
>
> 원문 아이디어(addyosmani/agent-skills, obra/superpowers, getsentry/skills, anthropics/skills)의 원칙을 참고했으나
> 본문은 MATE Builder 용도로 자체 저작(영문) — 외부 원문 미포함, 라이선스 의무 없음.

## Vendored 공개 스킬 (v7~v8, v9에서 자체 저작으로 대체)

다음 2종은 [anthropics/skills](https://github.com/anthropics/skills)의 **Apache-2.0** 스킬을
원문 그대로 벤더링한 것이다. 각 디렉터리에 원문 `LICENSE.txt`(Apache-2.0 전문)가 동봉되어 있고,
`SKILL.md` frontmatter의 `license:` 값이 `/license` 명령에 표시된다.

- `mcp-builder` — MCP 서버 구축 가이드 (Python/Node)
- `webapp-testing` — Playwright 웹앱 테스트 툴킷

> 동일 원본 repo의 `pdf` 스킬은 Anthropic 독점 라이선스(재배포 금지), `changelog-generator`는
> 라이선스 미표기로 권리 확인 불가 — 두 종은 번들에서 제외했다.
> `skill-creator`는 v9부터 원칙만 참조한 자체 저작 영문 SKILL.md 로 대체되어 라이선스 의무가 없어졌다.

## 스킬 추가/수정 후 zip 재생성
1. 이 폴더 아래 `<skill-name>/SKILL.md` 를 추가·수정한다.
2. zip 에 반영한다(기존 항목은 유지, 신규만 추가 — `references/`·`scripts/` 같은 하위 파일이 있으면 아래 스크립트 대신 폴더째 넣을 것):
   ```bash
   cd src/MoaiCode.Cli/Resources
   python3 - <<'PY'
   import zipfile, os
   src = "skills-src"
   with zipfile.ZipFile("bundled-skills.zip", "a", zipfile.ZIP_DEFLATED) as z:
       have = set(z.namelist())
       for name in sorted(os.listdir(src)):
           d = os.path.join(src, name)
           f = os.path.join(d, "SKILL.md")
           if os.path.isdir(d) and os.path.isfile(f) and f"{name}/SKILL.md" not in have:
               z.write(f, f"{name}/SKILL.md")
               print("added", name)
   PY
   ```
   (기존 항목을 교체하려면 zip 을 새로 만들거나 해당 항목을 지운 뒤 다시 추가한다.)
3. `BundledSkills.Version` 을 올린다 → 다음 실행 시 `~/.mate/bundled-skills` 로 **재추출**된다.
