# 픽셀 대조 — 앱(WebView2) vs 목업 (2026-10-26)

같은 데이터(VeriStand 로그 3개 중 Log File_2026_02_27_05_28_20, 차트 = Target Position · DEG · Sys_Man.ACT_Interval_Value · Sys_Man.IGV + 레인 command/Ready/Fault, X = System Time(relative), 커서 A 8.4 s / B 19.7 s, Target Position 선택)를 넣고
앱 캡처(`NexysView.exe --shot --scene veristand --mode VIEW|NAVIGATOR|ANALYSIS --probe 1`, WebView2 CapturePreview, 페이지 1440×900)와
목업 캡처(design/mockup/*.html의 오프라인 복사본 work/webview/mockup/*.html — Google Fonts 링크만 번들 폰트 @font-face로 교체 — 를 헤드리스 Chrome `--window-size=1440,900 --screenshot`)를 나란히 비교했다.
도구: tools/pixel_diff.py (영역별 평균 차·변경 픽셀·레이아웃 프로파일 상관, 구분선 y/패널 x 좌표 비교, DOM 프로브 app/web/public/probe.js 비교). 결과 시트 work/webview/pixel/{view,navigator,analysis}_diff.png (앱 | 목업 | 차이×3), 수치 work/webview/pixel/diff_summary.md, *_diff.json, *_probe.json.

## 1. 구조 (패널 위치·높이·구분선·폰트) — 기준: 구조 차이 0

| 화면 | 패널 세로 가장자리 x (앱 = 목업) | 1px 구분선 y (±2 px) | DOM 1px 규칙 (probe.js) | 폰트 크기 집합 | 구조 차이 |
|---|---|---|---|---|---|
| VIEW | 16 · 317 · 330 · 1109 · 1122 · 1423 (동일) | 18/18 일치, 앱에만 y=845 (속성 패널의 추가 섹션 PROPERTIES 하단선) | 22/22 일치 + 앱 추가 1 | 9·10·11·12·13·44 px 동일 (목업의 16 px = 화면 밖 sr-only label) | **1** (추가 섹션) |
| NAVIGATOR | 동일 | 18/18 일치 | 12/19 일치 — 불일치 7쌍은 전부 목업 프로브의 뷰포트 차이(아래 §3) | 9·10·11·12·13·34 px 동일 | **0** |
| ANALYSIS | 16 · 317 · 330 · 571 · 584 · 1109 · 1122 · 1423 (동일) | 17/17 일치 | 14/23 일치 — 불일치는 결과 카드 높이(플롯 종횡비 684:290, 카드 폭이 16 px 넓은 프로브 뷰포트에서 7 px 더 큼) | 9·10·11·12·13·30 px 동일 | **0** |

폰트 패밀리: 두 쪽 모두 Inter · JetBrains Mono · Instrument Serif (Noto Sans KR는 한글 폴백).
토큰: 배경 #ECEAE5 · 카드 #F7F6F3 · 보조 #EFEDE8 · 구분선 #DAD7D0 · 강조선 #B9B5AC · 글자 #17181A/#5F5E5A/#8F8C85 · 검정 알약 #17181A · 주황 #E8601C · 초록 #2E9E5B · 플롯 #121416/#2A2E33/#B8BDC4 · 트레이스 #FFD400 #00D1FF #4CFF4C #FF5CE1 #FF8A00 #8FA8FF #FF5A5A #B6FF00 — 목업 inline style에서 그대로 옮김(app/web/src/styles.css `:root`).
높이: 헤더 52+1, 메인 패딩 12/16 · 간격 12, 카드 헤더 36+1, 차트 헤더 40+1, 커서 표 헤더 20 · 행 22+1, 트리 행 30/26, 검색 30+2, 알약 22/24/26/30/32/36, 속성 큰 값 44 px 세리프, 분석 통계 30 px 세리프, 데이터 소스 통계 34 px 세리프, 푸터 24+1. 상자 모델은 목업과 같은 content-box (처음 border-box로 옮겼을 때 2~4 px 누적 오차 → 수정).

## 2. 영역별 픽셀 수치 (work/webview/pixel/diff_summary.md)

| 영역 | VIEW 평균차 / 변경% / 프로파일 상관 | NAVIGATOR | ANALYSIS | 판정 |
|---|---|---|---|---|
| 헤더 52px | 1.2 / 1.8 / 0.98 | 1.2 / 1.8 / 0.98 | 1.2 / 1.8 / 0.98 | OK (브랜드 2행 텍스트만 다름: 목업 "[LOGO.PNG 18PX]" 자리표시) |
| 모드 알약 | 0.3 / 0.3 / 1.00 | 동일 | 동일 | OK |
| 툴 버튼 | 2.0 / 3.2 / 0.99 | 동일 | 동일 | OK (UNDO/REDO 비활성 농도) |
| 좌측 NAVIGATOR 헤더+검색 | 0.2 / 0.1 / 1.00 | 동일 | 동일 | OK |
| 좌측 트리 행 | 15.8 / 16.8 / 0.52 | 15.7 / 16.6 / 0.52 | 15.6 / 16.5 / 0.52 | 데이터 차이: 목업은 채널명 축약("LGC_ANALYSIS.CO"), 계층 노드 OSM_Out 아래 2채널; 앱은 실제 이름 OSM_Out ▸ LGC_ANALYSIS ▸ CONI_Out ▸ IGVdem, 스파크라인은 곡선 색. 행 높이·들여쓰기(26 px, 42/58 px)는 동일 |
| 중앙 | 55.8 / 30.9 / 0.74 (플롯 = 데이터) | 0.8 / 0.9 / 1.00 | 2.0 / 2.3 / 0.94 | VIEW는 데이터 영역(곡선·커서 값), NAVIGATOR·ANALYSIS는 일치 |
| 중앙 카드 헤더 | 23.8 / 20.7 / 0.70 | 0.2 / 0.3 / 0.99 | 1.9 / 2.8 / 0.96 | VIEW 헤더: 목업 알약 "X · SYSTEM TIME"이 두 줄로 접힘(목업 HTML의 자연 줄바꿈), 앱은 한 줄 + 범례 4개 |
| 우측 속성 | 6.7 / 5.2 / 0.88 | 1.9 / 2.0 / 0.90 | 5.3 / 4.8 / 0.81 | 값(데이터)만 다름; 섹션 높이 동일 |
| 푸터 | 5.0 / 7.5 / 0.78 | 4.7 / 7.5 / 0.80 | 4.8 / 7.1 / 0.83 | 상태 문구(데이터) |

## 3. 남은 차이 목록 (전부 데이터 또는 목업 자체의 성격)

| # | 항목 | 앱 | 목업 | 분류 |
|---|---|---|---|---|
| 1 | 속성 패널 끝의 PROPERTIES 섹션(채널 원시 속성 7개) | 있음 (CURVES 아래, 하단선 y=845) | 없음 | 기능 추가(구조 +1, VIEW) |
| 2 | 곡선 값 | Target Position 34.00 … | 110.0 … | 데이터(목업 숫자는 가상) |
| 3 | 파일 이름 | Log File_2026_02_27_05_28_20 | Log File_2026_02_27_05_28 | 데이터(목업이 초 단위 생략) |
| 4 | 트리 계층 노드명 | 실제 점 분리 이름 | 축약 | 데이터 |
| 5 | 플롯 높이 | 카드 끝까지 채움 | SVG 종횡비(872:424)로 고정, 카드 아래 빈칸 | 목업의 정적 SVG 한계 — 차트 카드 flex:1 유지 |
| 6 | 레인 라벨 | 채널명 마지막 토큰 | command/Ready/Fault | 동일 규칙 |
| 7 | ANALYSIS 결과 카드 높이 | 플롯 종횡비 684:290을 CSS aspect-ratio로 재현 | 동일 | 일치(프로브 뷰포트 16 px 차이로 7 px) |
| 8 | 분석 파라미터 TYPE/ORDER | Biquad / 2 (엔진 능력) | Butterworth / 4 | 데이터(필드 구조 3×2 동일) |
| 9 | ZERO-PHASE 필드 | Q 필드 | ZERO-PHASE on | 엔진 능력(biquad Q) |
| 10 | 다크 테마 | v3 다크 토큰, 구조 동일 (work/webview/pixel/app_view_dark.png) | 없음 | 범위 밖 |

프로브 뷰포트: 목업 DOM 프로브는 헤드리스 `--dump-dom`에서 실행되어 뷰포트가 1424 px(스크롤바)로 잡혀 x 좌표·가변 폭 요소 높이가 조금 다르다. 스크린샷(--screenshot, 1440)에서 추출한 구분선 좌표는 세 화면 모두 x 전부 일치, y는 VIEW의 추가 섹션 1줄만 차이.
