# moai-code 프로젝트 지침

## 로깅 (Logging)
- **로그 출력은 반드시 영어(ASCII)로만 작성한다.** 한글 등 비-ASCII 문자를 로그 메시지에 넣지 말 것.
  - 이유: Windows 기본 코드페이지(CP949) 등 환경에서 로그 파일/콘솔이 깨지는 것을 방지.
  - 적용 대상: `MoaiLog`를 포함한 모든 로그 메시지, 예외 컨텍스트 문자열, 진단 출력.
  - 사용자 입력(요청 텍스트 등)은 비-ASCII일 수 있으므로 **원문을 로그에 넣지 말고** 길이·해시 등 ASCII 메타데이터로 대체한다.
  - 공용 로거는 `MoaiCode.Config.MoaiLog` (파일: `~/.moai/logs/moai.log`, 레벨은 `~/.moai/settings.json`의 `logLevel`).
