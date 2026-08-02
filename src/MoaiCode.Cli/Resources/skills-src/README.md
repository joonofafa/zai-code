# Bundled skill sources

이 폴더는 **moai-code가 자체 저작한 기본 번들 스킬의 원본(SKILL.md)**입니다. 빌드 시 실제로 배포되는 것은
같은 디렉터리의 `bundled-skills.zip`(임베디드 리소스)이며, 이 폴더는 그 zip 을 다시 만들 수 있게 소스를
추적하기 위한 것입니다.

## 현재 자체 저작 스킬
- `org-data-query` — 데이터함(OrgDatas) 읽기전용 SELECT 워크플로 (스키마 먼저 조회, 한글 식별자 따옴표 금지)
- `xunit-test-writer` — 변경 코드 xUnit 테스트 작성/보강
- `business-report` — 데이터/조직문서 → 네이티브 Docx/XlsxCreate 보고서 생성

> `bundled-skills.zip` 에는 이 외에 awesome-claude-skills 큐레이션(pdf, mcp-builder, changelog-generator,
> webapp-testing, skill-creator)도 들어 있으며, 그 원본은 여기서 관리하지 않고 zip 안에만 있습니다(라이선스 포함).

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
