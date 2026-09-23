# Bundled skill sources

이 폴더는 **zaiCode가 자체 저작한 기본 번들 스킬의 원본(SKILL.md)**입니다. 빌드 시 실제로 배포되는 것은
같은 디렉터리의 `bundled-skills.zip`(임베디드 리소스)이며, 이 폴더는 그 zip 을 다시 만들 수 있게 소스를
추적하기 위한 것입니다.

## 현재 자체 저작 스킬
- `xunit-test-writer` — 변경 코드 xUnit 테스트 작성/보강
- `business-report` — 데이터/조직문서 → 네이티브 Docx/XlsxCreate 보고서 생성
- `code-review` — 정확성·보안·호환성·성능·테스트 관점의 변경사항 검토 (v5)
- `systematic-debugging` — 재현→원인 분리→근본 원인→최소 수정→검증 절차 (v5)
- `source-driven-development` — 낯선 API는 설치된 소스·공식 문서로 검증 후 작성 (v5)

> 원문 아이디어(addyosmani/agent-skills, obra/superpowers, getsentry/skills)의 원칙을 참고했으나
> 본문은 zaiCode 용도로 자체 저작(영문) — 외부 원문 미포함, 라이선스 의무 없음.

## Vendored 공개 스킬 (v7~, zip 안에만 보관)

다음 3종은 [anthropics/skills](https://github.com/anthropics/skills)의 **Apache-2.0** 스킬을
원문 그대로 벤더링한 것입니다. 각 디렉터리에 원문 `LICENSE.txt`(Apache-2.0 전문)가 동봉되어 있고,
`SKILL.md` frontmatter의 `license:` 값이 `/license` 명령에 표시됩니다.

- `mcp-builder` — MCP 서버 구축 가이드 (Python/Node)
- `skill-creator` — 스킬 작성·개선·벤치마크 도구
- `webapp-testing` — Playwright 웹앱 테스트 툴킷

> 동일 원본 repo의 `pdf` 스킬은 Anthropic 독점 라이선스(재배포 금지), `changelog-generator`는
> 라이선스 미표기로 권리 확인 불가 — 두 종은 **번들에서 제외**했다.

## 스킬 추가/수정 후 zip 재생성
1. 이 폴더 아래 `<skill-name>/SKILL.md` 를 추가·수정한다.
2. zip 에 반영한다(기존 항목은 유지, 신규만 추가):
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
3. `BundledSkills.Version` 을 올린다 → 다음 실행 시 `~/.moai/bundled-skills` 로 **재추출**된다.
