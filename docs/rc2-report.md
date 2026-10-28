# Nexys VIEW 1.0.0-rc.2 — 저장소 분리 보고 (2026-10-29)

## 바뀐 것
- **엔진이 nexys-core submodule(`app/core`)로**: `NexysView.Core`(형식·버스·ICD), `NexysView.Render`(플롯), `NexysView.Host`(EngineBase · Bridge · FrameServer · AppState · Mil1553Service · HostCommands · WebShell · Diag), `NexysView.Web`(페이지 공통 `@core`). 앱에는 `Engine : EngineBase`(분석·보고서·.tdv·CSV/TDMS 내보내기), 가져오기·플러그인·셀프테스트만 남는다. `Directory.Build.props`의 `CoreDir`와 Vite `@core` alias가 submodule 경로를 가리킨다.
- **BUS 모드·ICD 모듈은 NexysBus로**(자매 앱, 같은 엔진). `.mon`을 열면 "NexysBus에서 열기" 안내 → 설치돼 있으면 그 파일로 실행, 없으면 릴리스 페이지.
- 설정에서 BUS 시간 표시·ICD 모듈·템플릿 폴더 행 제거, 도움말에서 BUS·ICD 절 제거(9절 → 7절, legacy 포함 9), `--v0100`·`--v100rc` 셀프테스트는 NexysBus로 이동. 스트레스 묶음에서 버스 캡처 제외(TDMS·DXD).

## 회귀 (개발 빌드, 같은 PC)
| 묶음 | 결과 |
|---|---|
| `--v094` | 23/23 PASS |
| `--selftest` | 전 관문 PASS, memory_ws_mb 526.8 MB (≤ 600) |
| `--stress --iter 50` | 예외 0 · 정지 0 · 메모리 +34 MB · 창 12 열고 잔여 0 · 118.5 s |
| 배포 빌드 스모크 `--v094` | 포터블 2회 23/23 · 설치 폴더 23/23 |
| 저장소 관문 | 세 저장소 각각 clone → `submodule update --init` → `dotnet build` 성공, 타사 이름 0(작업 트리·전체 히스토리), 바이너리 0 |

## 산출
- `NexysView-1.0.0-rc.2-setup.exe` 61,863,177 B (sha256 b75412a2…) · `NexysView-1.0.0-rc.2-portable.exe` 82,270,484 B (sha256 1e00fddc…).
- nexys-core 74a95e5 · nexys-view feature/mil1553 263637c.

## 결정 항목
- 뷰어에 BUS 모드를 남길지(현재: 안내만). 앱 이름 NexysBus(임시). main 머지는 1.0.0 때.
