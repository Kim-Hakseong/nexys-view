# Nexys VIEW 0.9.0 / 0.9.1 / 0.9.2 beta — 배포 보고 (2026-10-26)

입력: beta.md. 기반: docs/webview-report.md(WebView2 전환). 저장소 https://github.com/Kim-Hakseong/nexys-view (private) · Release https://github.com/Kim-Hakseong/nexys-view/releases/tag/v0.9.0-beta

## 1. 수정 2건

### a) 로고
- 페이지 헤더 좌상단: 검정 원 + "N" + 두 줄 텍스트 → `design/brand/Logo.png`(233×29 워드마크) 한 장, 높이 18 px·비율 유지(`height:18px;width:auto`), 옆에 "0.9.0 BETA" 10 px. 클릭하면 정보 대화상자.
- 고해상도: 원본이 233×29라 18 px 기준 1.6×까지 선명(2× = 36 px 높이에는 29 px 원본이 확대됨). 2× 전용 자산이 필요하면 `design/brand/Logo@2x.png`(466×58)를 추가하고 `srcset`만 붙이면 된다 — 자산이 없어 현재는 단일 파일.
- 다크 테마에서는 워드마크의 회색 글자 대비를 위해 `filter: brightness(1.15)`.
- 창 아이콘·작업 표시줄 아이콘(AppIcon.ico, AUMID)은 그대로.

### b) 다크 테마 포인트 색 라임(#D9F23A) → #7A1020
라임은 `app/web/src/styles.css`의 다크 토큰 `--accent` 한 곳에서만 쓰였고(캔버스·WPF 코드에는 없음), 그 토큰을 쓰는 자리는 아래와 같다. 라이트 테마(`--accent: #17181A`)는 변경 없음.

| 쓰임 | 종류 | 적용 색 | 글자/대비 |
|---|---|---|---|
| 활성 모드 탭 (NAVIGATOR/VIEW/ANALYSIS/REPORT) | 면(채움) | #7A1020 | 글자 #FFFFFF, 10.9:1 |
| 주 버튼 알약: OPEN FILE, RUN, USE … AS X, DROP ALL (n), COMPARE, CLOSE(정보) | 면 | #7A1020 | 글자 #FFFFFF |
| 선택 상태 알약: TREE/FLAT, REL/ABS/NUMERIC, GRID, DIGITAL LANES, STEP/LINE/RIGHT AXIS, START TIME/ZERO/MANUAL, 결과 함수 | 면 | #7A1020 | 글자 #FFFFFF |
| 비교 대화상자 체크박스 (`accent-color`) | 면 | #7A1020 | 체크 흰색 |
| 로고 원판 배경 | 면 | 제거(워드마크로 교체) | — |
| 키보드 포커스 링 (`:focus-visible`, 2 px) | 선 | **#B2323F** (밝은 톤) | 배경 #0E0F11 대비 3.1:1, 패널 #1E2126 대비 2.6:1(3:1 미만이지만 규칙대로 밝은 톤 적용, 더 밝히면 색상 이탈) |
| 1 px 선·11 px 이하 텍스트에 포인트 색 사용 | — | 해당 없음 | 알약 안 10~11 px 글자는 흰색(면 위) |
| LED/상태(READY·BUSY·ERROR) | — | 변경 없음 (#2E9E5B / #E08A1E / #D2423C, 라임 미사용) | |
| 커서(다크 플롯 #FFFFFF, 라이트 플롯 #E8601C), 드롭 미리보기(#E8601C), Δ 값(#E8601C) | — | 변경 없음 (라임 미사용) | |

잔여 검사: `grep -rni D9F23A app/web/src app/NexysView.App app/NexysView.Render` → 0건, 빌드된 `app/web/dist/assets/index.css` → 0건 (이전 WPF 토큰 파일은 work/legacy_wpf로 옮겨져 빌드에 포함되지 않음).
캡처: work/beta/shots/dark_view.png, 저장소 docs/images/dark.png.

## 2. 패키징

| 항목 | 결과 |
|---|---|
| 버전 | Directory.Build.props `Version 0.9.0-beta`, AssemblyVersion/FileVersion 0.9.0.0, 창 제목 "Nexys VIEW 0.9.0 beta"(AppInfo.VersionText), 페이지 헤더 "0.9.0 BETA", layout.json에 `Version` 필드 |
| 정보 대화상자 | `F1` 또는 로고 클릭: 버전, 베타 안내, GitHub Issues·저장소 링크(기본 브라우저로 열림, 호스트 `openUrl`), 폰트·라이브러리 고지 |
| 설치형 | `app/dist/NexysView-0.9.0-beta-setup.exe` **58.8 MB** (Inno Setup 6.7.3, lzma2/ultra64). Program Files 설치, 시작 메뉴·바탕화면(선택) 바로가기(AppIcon), 언인스톨, 파일 연결 .tdms/.tdm/.dxd 선택(기본 해제), WebView2 런타임 없으면 번들 Evergreen 부트스트래퍼(`MicrosoftEdgeWebview2Setup.exe /silent /install`) 실행, 라이선스 폴더 `licenses\` |
| 포터블 | `app/dist/portable/NexysView.exe` **82.1 MB** 1개 (PublishSingleFile + IncludeNativeLibrariesForSelfExtract + IncludeAllContentForSelfExtract, 압축). 첫 실행 시 `%TEMP%\.net\NexysView\`에 풀림. 더블클릭·실시간 테스트 시스템 파일 열기 확인(work/beta/portable_test/portable_view.png). Python DataPlugin 워커는 미포함 |
| 설치 검증 | Windows 샌드박스: 기능 미설치(Containers-DisposableClientVM 상태 없음) · 새 사용자 계정: 관리자 권한 없이 만들 수 없어 생략. 대신 현재 PC에서 **설치 파일을 무인 설치(`/VERYSILENT /CURRENTUSER`, 새 폴더) → 설치본으로 실시간 테스트 로그 열기(`--shot`, 페이지 준비·차트 표시 확인) → 무인 언인스톨(폴더 제거 확인)** 까지 수행(work/beta/install_test/). WebView2 런타임이 없는 PC에서의 부트스트래퍼 경로는 코드·스크립트 검토만(이 PC에는 155.0.4283.62가 있음) |
| 폰트 라이선스 | installer/licenses/ (OFL-Inter, OFL-NotoSansKR, OFL-InstrumentSerif, OFL-JetBrainsMono, THIRD-PARTY.txt) → 설치 폴더 `licenses\`, 저장소 installer/licenses/, README 라이선스 절 |
| 앱 라이선스 | installer/LICENSE.txt(평가용 베타 라이선스, 한/영) → 설치 마법사 라이선스 페이지, 저장소 LICENSE.txt |

## 3. GitHub 프라이빗 저장소
- 범위: `tools/export_repo.py`가 app/(bin·obj·dist·node_modules 제외, 테스트는 로컬 기준 데이터가 필요 없는 것만), design/(DESIGN 문서·mockup·brand), installer/(redist 제외), docs 5건(webview-report, v3-report, final-report, pixel-diff, beta-report), tools/pixel_diff.py, README/LICENSE/.gitignore, docs/images(앱 캡처 4장)를 `C:\rea-work\nexys-view`로 내보낸다. ref/, work/, poc/, REA 도구, 사용자 로그 파일, dist, 타사 바이너리는 복사하지 않고 .gitignore로도 막는다.
- 커밋 전 검사(work/beta/export_scan.txt): 타사 이름 0건(내보내기 때 주석·문서의 타사 이름을 "기준 도구/the vendor/DXD" 등 중립어로 치환), 바이너리 0건. 예외 6건은 파일 형식 스키마 URI(`www.ni.com/Schemas/USI`)와 DataPlugin SDK C++ 네임스페이스(`ni::dataplugin`)로 형식·SDK 식별자이며 산문의 회사명이 아님.
- 내보낸 트리 단독 빌드 확인: `npm install && npm run build` → `dotnet build`(App, Tests) 오류 0 (.dxd 리더는 외부 프로젝트가 없으면 빠지도록 `DXD` 조건부 컴파일).
- `.dxd` 리더가 의존하는 외부 프로젝트와 관문 테스트(로컬 기준 데이터 필요)는 저장소에 없다 — README 개발 절에 명시.
- 저장소: https://github.com/Kim-Hakseong/nexys-view (private), 첫 커밋 + 태그 v0.9.0-beta, Release에 설치 파일·포터블 exe 첨부.
- README: 참조 저장소(testbench-plc-sim)의 구조(제목 → 한 줄 소개 → 기술 스택 → 첫 캡처 → 설치와 실행 → 화면별 절 → 표 → 알려진 문제 → 라이선스)와 평서체 어조를 따름. 상단 로고·배지(버전·플랫폼), 캡처 3장(docs/images), 성능 표는 "기준 상용 도구" 대비로 표기(타사 이름·역분석 과정은 쓰지 않음).

## 4. 동료 배포용 안내문 (초안)
> Nexys VIEW 0.9.0 베타를 공유합니다. TDMS/TDM 측정 로그 뷰어로, 실시간 테스트 로그의 System Time을 X축으로 바로 쓰고 1,000만 샘플 곡선도 끊김 없이 움직입니다.
> 설치: https://github.com/Kim-Hakseong/nexys-view/releases 에서 `NexysView-0.9.0-beta-setup.exe`(설치형) 또는 `NexysView-0.9.0-beta-portable.exe`(폴더에 두고 실행)를 받으세요. Windows 10/11 64비트, WebView2 런타임은 없으면 설치 중 자동으로 받습니다.
> 써 보기: 파일을 창에 드롭 → 채널을 차트에 드롭 → 아래 x축 영역에 System Time을 드롭하면 시간축이 됩니다. 휠 줌, 드래그 이동, 클릭 커서 A / Shift+클릭 커서 B.
> 실시간 테스트 로그 파일로 써보기 — X축 시간 채널(헤더의 USE SYSTEM TIME AS X), 디지털 레인(command/Ready/Fault 같은 상태 신호), 파일 비교(NAVIGATOR → COMPARE n FILES, 시작 시각/0 s/수동 정렬)가 핵심 기능입니다.
> 피드백은 GitHub Issues로 (앱 정보 대화상자 F1에 링크). 멈춤·오류는 이벤트 로그 캡처(이벤트 뷰어 → Windows 로그 → 응용 프로그램, NexysView 항목)와 재현 순서를 함께 올려 주세요.
> 베타라 화면과 기능이 바뀔 수 있습니다. Issues에는 Windows 버전·파일 종류(채널·샘플 수)도 적어 주시면 빠르게 봅니다.
> 알려진 제한: 메모리 사용량이 큽니다(10곡선×10M 기준 약 1.1 GB), 포터블에는 Python DataPlugin이 없습니다.

## 5. 0.9.1 beta (2026-10-26) — 베타 피드백 수정 2건

Release https://github.com/Kim-Hakseong/nexys-view/releases/tag/v0.9.1-beta · 캡처 폴더 work/beta/icons/

### a) 작업 표시줄·작업 관리자 아이콘 (포터블·설치형 공통)
- 증상 재현: 포터블 exe와 설치본 모두 **작업 표시줄 버튼과 작업 관리자 행이 흰 빈 문서 아이콘**. Alt+Tab(창 아이콘)과 탐색기(exe 아이콘)는 정상. 캡처 `p1_taskbar_zoom.png`(포터블), `p1_taskmgr_top.png`("NexysView.exe" 행, 빈 아이콘), `cmp_taskbar.png`(위: 포터블, 가운데: 설치형 dist/app, 아래: AUMID 끈 포터블).
- 진단 (모두 읽기 전용 검사, ResourceHacker 없이):

| 검사 | 결과 |
|---|---|
| PE 아이콘 리소스 (`tools/pe_icons.py`, stdlib PE 파서) | dist/app·dist/portable·bin 모두 RT_GROUP_ICON 32512 1개 = 16/24/32/48/64/256 px @32bpp 6장. **단일 파일 apphost에도 포함** → ApplicationIcon 누락 아님 |
| 셸 추출 (`ExtractAssociatedIcon`, `ExtractIconEx(exe,0)`) | 두 exe 모두 32×32 NXS 아이콘 반환 (`portable_assoc.png`, `regular_assoc.png`) → 탐색기·파일 연결 아이콘은 정상 |
| 창 아이콘 (Alt+Tab, `p1_screen.png`) | 정상 |
| 창 AppUserModel 속성 저장소 (`SHGetPropertyStoreForWindow` 읽기) | ID=`Nexys.NexysView`, RelaunchCommand=`"<exe>"`, RelaunchIconResource=`<exe>,0` — 이미 `Environment.ProcessPath` 기준으로 기록돼 있었음(Assembly.Location 아님) |
| AUMID 끄고 실행 (`NEXYS_NO_AUMID=1`) | 작업 표시줄 NXS 아이콘 **정상** (`cmp_taskbar.png` 3행) |
| 실행 중 창의 속성 값을 바꿔 가며 버튼 재생성 (`icon_probe2/3.ps1`) | RelaunchIconResource = `<exe>,0` · `<exe>,-32512` · `@<exe>,-32512` · `AppIcon.ico,0` · 새 경로 복사본 `,0` · 심지어 `shell32.dll,-16` 까지 **전부 빈 문서 아이콘**; ID 키를 지우는 순간 NXS 아이콘 복귀 (`cmp_probe2.png`, `cmp_probe3.png`) |

- 결론(확정): 이 PC(Windows 11 26H2 빌드 26300, 2026-10-26)의 작업 표시줄과 작업 관리자는 **명시적이지만 등록되지 않은 AppUserModelID**를 가진 창의 아이콘을 창(WM_GETICON)·exe·RelaunchIconResource 어디에서도 가져오지 않고 기본 문서 아이콘을 그린다. docs/design-report.md §2의 "속성 저장소에서 읽는다"는 결론은 현재 재현되지 않아 **정정**(같은 OS 빌드 번호이지만 당시 캡처와 반대 — 월간 셸 업데이트 차이로 추정, UNVERIFIED).
- 수정: `Services/AppId.cs`의 `SetCurrentProcessExplicitAppUserModelID` + 창 속성 저장소 기록을 **기본 비활성**(진단용 opt-in `NEXYS_AUMID=1`). 명시적 ID가 없으면 Windows가 exe 경로로 AUMID를 만들어 그룹·고정이 되고 exe에 박힌 아이콘을 쓴다.
- 결과: 포터블·설치형 모두 정상 — 작업 표시줄 `final_portable_taskbar_zoom.png` / `final_installed_taskbar_zoom.png`, 작업 관리자 `final_portable_taskmgr.png`(행 "NexysView", NXS 아이콘) / `final_installed_taskmgr.png`, 전/후 비교 `cmp_taskmgr_rows.png`. (작업 관리자 행 이름도 "NexysView.exe" → 파일 설명 "NexysView"로 바뀜.)
- 캡처 도구: `tools/capture_shell.ps1`(작업 표시줄 띠·Alt+Tab·작업 관리자). 작업 관리자는 자동 승격되면 입력·종료가 막히므로(UIPI) `runas /trustlevel:0x20000`으로 띄운다. 이미 떠 있는 인스턴스는 단일 인스턴스라 재사용되며 스크립트가 종료하지 못할 수 있음(수동으로 닫기).

### b) 첫 실행 기본 테마 = 라이트
- 설정 파일(`%LOCALAPPDATA%\NexysView\layout.json`)이 없으면 **라이트 크롬 + 다크 플롯**(`Engine.DefaultTheme`/`DefaultPlot` 상수 한 곳). 저장된 테마는 그대로 복원, 잘못된 값("light"/"dark" 외)은 기본값으로.
- 검증용 `--fresh`: layout.json을 읽지도 쓰지도 않고 WebView2 프로필을 `WebView2-fresh`로 분리해 첫 실행과 같은 상태로 뜬다(사용자 설정 보호 규칙 유지). 캡처 `final_portable_screen.png`(포터블 `--fresh`, 창 제목 "Nexys VIEW 0.9.1 beta", 라이트).

### c) 패키징 0.9.1

| 항목 | 결과 |
|---|---|
| 버전 | 0.9.1-beta (Directory.Build.props, AppInfo 폴백, 설치 스크립트, LICENSE, README) |
| 설치형 | `app/dist/NexysView-0.9.1-beta-setup.exe` 61.7 MB (61,739,076 B) |
| 포터블 | `app/dist/NexysView-0.9.1-beta-portable.exe` 82.1 MB (82,140,684 B) |
| 검증 | 무인 설치(`/VERYSILENT /CURRENTUSER /DIR=work\beta\install_test\0.9.1`, 종료 코드 0) → 설치본 실행·캡처 → 무인 언인스톨(종료 코드 0, 폴더·언인스톨 키 제거 확인). 테스트 514/514 (`work/bench/gate_tests_091.txt`) |

## 6. 0.9.2 beta (2026-10-26) — 0.9.1 피드백 수정 3건 + README REPORT 절

- 증상: 새로 받은 0.9.1 포터블을 열자 시간축 로그 3개가 이미 열려 있음(사용자 캡처). 원인은 기본 설정이 아니라 **layout.json의 이전 세션 파일·차트 복원**(같은 PC에서 이전에 열어 둔 파일). 새 PC에서는 비어 있었을 것이나, 요구대로 **이전 세션 파일·차트 복원을 끄고**(`LayoutStore.RestoreSession=false`, 창 크기·테마·모드·트리 계층만 복원) 시작 시 **내장 샘플**을 연다.
- 내장 샘플 `Services/SampleData.cs`: Core `TdmsWriter`로 합성 TDMS(그룹 Sample, Time·Sine 1 Hz·Sine 5 Hz·Ramp·Temperature(double), Square·Counter(I32), State(U8), 20,000 샘플 @1 kHz, 981 KB)를 `%LOCALAPPDATA%\NexysView\sample\Sample.tdms`에 한 번 쓰고(`Sample.rev` 개정 번호로 재생성), 명령줄 파일이 없을 때 연다. `--fresh`/자동화는 `%TEMP%\NexysView\sample\`에 써서 사용자 데이터 보호 규칙 유지. 캡처 work/beta/shots/firstrun_sample.png(`--fresh` 첫 실행: 샘플 1파일·8채널, 라이트).
- 제품명 라벨 제거: `Engine.FileDto`의 writer("실시간 테스트 시스템" 추정 라벨·파일 name/author 속성) 제거 → 데이터 소스 카드는 형식 칩만, 속성 패널 "writer" 행 삭제(work/beta/shots092/t_nav.png). 검색창 예시 `Sys_Man.*` → `Sine*`. 셀프테스트 장면 이름 `--scene timelog` → `timelog`(옛 이름도 받음), 내보내기 치환표에 실시간 테스트 시스템 → "실시간 테스트 시스템"(문서·주석) 추가.
- README: 첫 실행 절(샘플 캡처), REPORT 절을 블록 쌓기 → A4 미리보기 → PNG/PDF 순서로 캡처 2장(앱 REPORT 모드, 내보낸 PNG 페이지)과 함께 확장. 캡처는 `--shot --open Sample.tdms --curves 3 --cursors 1 --mode REPORT --report 1 --report-out <png>`(새 옵션 `--report`: 제목·텍스트·차트·표 블록 추가, `--report-out`: ExportTo PNG).
- **도움말 + 기능 투어** (참조: 사용자가 제시한 문서 검색 앱의 HelpModal/OnboardingTour 패턴 — 섹션 탭 가이드 모달 + 스포트라이트 투어; 구현은 `app/web/src/help.ts`, 프레임워크 없이 DOM 직접):
  - 투어 9단계(환영 → 모드 탭 `.modes` → OPEN → 트리 `.nav` → 차트 `.view` → 속성 `.props` → 분석 `.fns`(ANALYSIS로 전환) → 보고서 `.rep-left`(REPORT로 전환) → `?` 버튼). 대상 요소를 `box-shadow 0 0 0 9999px` 스포트라이트로 비추고 카드(380 px)를 right/left/bottom/top 선호 + 화면 경계 flip, 어느 쪽도 안 맞는 큰 대상(차트)은 대상 중앙에 겹쳐 배치. 진행 막대, 건너뛰기/이전/다음/시작하기, `→` `←` `Esc`, 배경 클릭 닫기. 투어가 모드를 바꾼 경우 끝나면 시작 모드로 복귀.
  - 첫 실행 자동 시작: 스냅샷 `app.tourSeen`(Engine.TourSeen ← layout.json `TourSeen`)이 false이고 `app.automation`(SelfTest.Enabled)이 아니면 1.2 s 뒤 시작, 끝/건너뛰기 시 브리지 `tourSeen` → 저장. `--fresh`는 저장하지 않으므로 매번 뜬다(캡처 work/beta/shots/firstrun_tour.png).
  - 사용 가이드 모달(760×540, `?` 버튼·`F1`; 정보 대화상자는 로고 클릭으로 유지): 시작하기(기능 투어 다시 보기 버튼, 처음 사용 4단계, 샘플 설명, Issues 링크) · 화면 구성 · 차트 조작(드롭 위치 표, 보기 표, 시간축·계단) · 분석(함수별 카드) · 보고서 · 단축키(kbd 표) · 활용 팁. 내용은 README와 같은 사실만.
  - 캡처 옵션 `--shot … --tour n` / `--help section`(`nx.test.tour/help`). 캡처 work/beta/shots092/tour_0…8.png, tour_dark.png, help_start/keys/chart.png. 헤더에 HELP를 글자 버튼으로 넣자 1440 px에서 PLOT · DARK가 두 줄로 꺾여 **아이콘 전용 `?` 버튼** + `.tool` nowrap으로 수정.
- 패키징 0.9.2(도움말 포함 최종): `app/dist/NexysView-0.9.2-beta-setup.exe` 61.8 MB (61,764,852 B), `app/dist/NexysView-0.9.2-beta-portable.exe` 82.1 MB (82,149,569 B). 테스트 514/514 — 병렬 부하 중 P3 렌더 관문 1건이 18.2 ms로 떨어졌으나 단독 재실행에서 15.92 ms로 통과(관문 16, 부하 민감, Render 코드 변경 없음; work/bench/gate_tests_092.txt). `--v3` 관문 fail=0 (work/beta/v3_092/gates.txt), 무인 설치(work/beta/install_test/0.9.2) → 설치본 `--fresh` 실행(제목 "Nexys VIEW 0.9.2 beta") → 무인 언인스톨(폴더 제거) 확인. 내보내기(C:\rea-work\nexys-view) 완료, 커밋 d721d5f, 태그 v0.9.2-beta, Release https://github.com/Kim-Hakseong/nexys-view/releases/tag/v0.9.2-beta (설치형 61,764,852 B · 포터블 82,149,569 B 첨부, 저장소는 2026-10-26부터 임시 public).

## 7. 0.9.3 beta (2026-10-26) — VIEW 탐색 도구 집중
동료 피드백(v093.md) 반영: ANALYSIS·REPORT 탭 숨김(플래그), 그룹/파일 드롭 → 차트, 오른쪽 드래그 영역 줌 + Backspace 히스토리, 밴드 커서 + 판독 띠, 개요/상세 연동, X축 링크, 축·곡선 설정, 가상 테이블 + Ctrl+C, 밴드 구간 CSV/TDMS·차트 PNG. 사용법·캡처·관문 수치는 **docs/093-report.md**.
