# Nexys VIEW 0.9.0 beta — 배포 보고 (2026-10-26)

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
| 포터블 | `app/dist/portable/NexysView.exe` **82.1 MB** 1개 (PublishSingleFile + IncludeNativeLibrariesForSelfExtract + IncludeAllContentForSelfExtract, 압축). 첫 실행 시 `%TEMP%\.net\NexysView\`에 풀림. 더블클릭·VeriStand 파일 열기 확인(work/beta/portable_test/portable_view.png). Python DataPlugin 워커는 미포함 |
| 설치 검증 | Windows 샌드박스: 기능 미설치(Containers-DisposableClientVM 상태 없음) · 새 사용자 계정: 관리자 권한 없이 만들 수 없어 생략. 대신 현재 PC에서 **설치 파일을 무인 설치(`/VERYSILENT /CURRENTUSER`, 새 폴더) → 설치본으로 VeriStand 로그 열기(`--shot`, 페이지 준비·차트 표시 확인) → 무인 언인스톨(폴더 제거 확인)** 까지 수행(work/beta/install_test/). WebView2 런타임이 없는 PC에서의 부트스트래퍼 경로는 코드·스크립트 검토만(이 PC에는 155.0.4283.62가 있음) |
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
> Nexys VIEW 0.9.0 베타를 공유합니다. TDMS/TDM 측정 로그 뷰어로, VeriStand 로그의 System Time을 X축으로 바로 쓰고 1,000만 샘플 곡선도 끊김 없이 움직입니다.
> 설치: https://github.com/Kim-Hakseong/nexys-view/releases 에서 `NexysView-0.9.0-beta-setup.exe`(설치형) 또는 `NexysView-0.9.0-beta-portable.exe`(폴더에 두고 실행)를 받으세요. Windows 10/11 64비트, WebView2 런타임은 없으면 설치 중 자동으로 받습니다.
> 써 보기: 파일을 창에 드롭 → 채널을 차트에 드롭 → 아래 x축 영역에 System Time을 드롭하면 시간축이 됩니다. 휠 줌, 드래그 이동, 클릭 커서 A / Shift+클릭 커서 B.
> VeriStand 로그 파일로 써보기 — X축 시간 채널(헤더의 USE SYSTEM TIME AS X), 디지털 레인(command/Ready/Fault 같은 상태 신호), 파일 비교(NAVIGATOR → COMPARE n FILES, 시작 시각/0 s/수동 정렬)가 핵심 기능입니다.
> 피드백은 GitHub Issues로 (앱 정보 대화상자 F1에 링크). 멈춤·오류는 이벤트 로그 캡처(이벤트 뷰어 → Windows 로그 → 응용 프로그램, NexysView 항목)와 재현 순서를 함께 올려 주세요.
> 베타라 화면과 기능이 바뀔 수 있습니다. Issues에는 Windows 버전·파일 종류(채널·샘플 수)도 적어 주시면 빠르게 봅니다.
> 알려진 제한: 메모리 사용량이 큽니다(10곡선×10M 기준 약 1.1 GB), 포터블에는 Python DataPlugin이 없습니다.
