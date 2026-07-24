# MoAI Desktop 배포 로그

Windows 데스크톱(Avalonia, 단일 exe) 릴리스 이력. CLI(`moai`)와 독립 버전.
배포본: `~/shareHub/Zone/MoAiDesktop_<버전>.exe` (self-contained, win-x64).

| 버전 | 요약 |
|------|------|
| 0.4.17 | **아이콘 lucide 통일**(open-moai와 동일): 첨부·문서함·전송·설정·새로고침·새작업·계정·편집 칩을 2px 라인 아이콘으로 교체 |
| 0.4.16 | 편집 세션 진입 시 **추천 질문 버튼**, 활성 대상 칩·입력창 **채팅 본문과 중앙 정렬**, 답변 전 바운스 인디케이터 확인 |
| 0.4.15 | **좌패널 UX 통합안**(리서치 기반): 활성 대상 칩(대상·적용범위·해제), 열린 문서=런처, 대화 기록 유형 배지+파일명, 세션 유형/대상 저장 · **미리보기 게이트**(라이브 편집 전 적용/취소) |
| 0.4.13 | 도구 진행 배지 **구체 문구**(슬라이드 읽는 중/편집 중 등) |
| 0.4.12 | 진행 배지 **FIFO 매칭**(잔존 버그 수정) + **바운스 애니메이션** |
| 0.4.11 | PowerPoint 서식 편집 확장: **set_fill/set_font/set_line** + 현재 선택 도형 대상 |
| 0.4.10 | COM `dynamic` 캐스팅 크래시 수정 (**MsoTriState는 bool 아님, 좌표는 Single**) |
| 0.4.9 | COM 편집 실패 **상세 진단 로그**(HRESULT·apartment·예외 스택) |
| 0.4.8 | 오피스 런처에서 **열린 문서 선택 → 편집 세션 시작**(COM 활성화+채팅) |
| 0.4.7 | 홈 화면 재설계(새 대화/오피스 문서 작업 타일) · **Markdown.Avalonia 제거 → 네이티브 MarkdownBlock**(Avalonia 11.2 StaticBinding 크래시 해결) |
| 0.4.6 | 레벨별 파일 로거 `MoaiLog`(~/.moai/logs/moai.log, settings.json `logLevel`) + 전역 미처리 예외 훅 |
| 0.4.5 | SPA 재설계 최초(인-윈도우 로그인/설정, 좌패널 Office/기록 분할, MoAI 마크다운 말풍선) |
| ~0.4.4 | 초기 데스크톱(로그인·프록시·모델 선택·이미지 생성·문서 삽입·트레이 상주) |

## 로그·진단
- 파일 로그: `~/.moai/logs/moai.log` (레벨 `~/.moai/settings.json`의 `logLevel`, 기본 info; 상세 진단은 `"logLevel":"debug"`)
- 로그 메시지는 영어(ASCII) 전용 — CP949 등에서 깨짐 방지

## 알려진 제약
- COM Office 편집은 **Windows 전용**(리눅스 빌드/실행은 되지만 COM 목록은 비어 있음)
- 미리보기 게이트는 "무엇을 바꿀지" 요약 확인 방식 — 시각적 diff/undo는 미구현(검토 중)
