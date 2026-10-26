# Nexys VIEW — WebView2 하이브리드 전환 보고 (webview, 2026-10-26)

결정: UI 계층을 WPF 컨트롤 트리에서 **WebView2(Chromium) 페이지**로 교체. 엔진(NexysView.Core: 리더·피라미드·분석·수식·플러그인 호스트, NexysView.Render: 모델·틱·보고서 Skia)은 그대로.
정답 화면 = design/mockup/view.html · navigator.html · analysis.html (픽셀 값 그대로 CSS로 옮김, 해석 없음).
문서: docs/pixel-diff.md(픽셀 대조) · work/webview/(캡처·관문) · app/web/(프론트 소스) · app/NexysView.App/Services/{Engine,Bridge,FrameServer,AnalysisService,ReportService,Compare}.cs.

## 1. 구조

```
NexysView.exe (WPF, 창 1개)                              WebView2 (msedgewebview2.exe ×N, Evergreen 155)
┌────────────────────────────────────────────┐          ┌─────────────────────────────────────────────┐
│ MainWindow.xaml  : WebView2 전체 화면        │          │ https://app.nexys/index.html  (wwwroot ↔ 가상 호스트) │
│   · 창 아이콘·AUMID(AppId.cs) 기존 코드 유지   │          │ app/web (Vite + TypeScript, 프레임워크 없음)          │
│   · 파일 대화상자, TDMS 저장, PNG/PDF 내보내기  │ JSON 명령 │   main.ts     셸(헤더·모드·푸터), 키보드, 파일 드롭, 테스트 훅 │
│ Services/Bridge.cs : WebMessage ⇄ Engine    │◀────────▶│   navigator.ts 트리(가상 스크롤, 계층·와일드카드·DROP ALL) │
│   · {id,cmd,…} → 디스패치 → {ev:'reply'}      │ state/   │   view.ts      차트 카드 목록·값 테이블·드롭 존             │
│   · Engine.Changed → {ev:'state'} (파일 트리는 │ undo/    │   plot.ts      캔버스 렌더러 + 제스처 + D&D 존              │
│     FilesVersion이 바뀔 때만 포함)             │ status/  │   properties.ts / navmode.ts / analysis.ts / report.ts   │
│   · 차트마다 공유 메모리 버퍼 8 MB(Float32)     │ frame    │   bridge.ts    postMessage · sharedbufferreceived          │
│ Services/FrameServer.cs : 뷰포트별 프레임     │◀────────▶│ 캔버스 2D: 격자/점격자, 다축, 포락선 열/폴리라인, 레인, 커서  │
│   · 픽셀 열당 min/max 포락선(피라미드) 또는     │ Float32  └─────────────────────────────────────────────┘
│     raw 샘플(≤6 samples/px), 레인 0/1,       │
│     y 자동 범위·눈금, x 눈금(시간 모드), 판독   │
│ Services/Engine.cs : AppState + 차트 모델    │
│   (PlotModel) + 선택 + Undo + 비교 + 테이블    │
│ AnalysisService / ReportService(Skia 페이지) │
│ NexysView.Core / NexysView.Render (불변)     │
└────────────────────────────────────────────┘
```

- **명령**(페이지→호스트, 60여 개): open/closeFile/setMode/setTheme/setPlot/select/selectFiles/setHier, addChart/removeChart/swapCharts/addCurves(zone plot|left|right|x|xy)/removeCurve/moveCurve/setView/zoomFull/setCursor/clearCursors/setCurve/setChart/setX/clearX/undo/redo/dropAll/compare/setAlign, table.*, analysis.*(function/param/inputs/run/cancel/show), report.*(add/remove/move/text/clear/page/export), openDialog/openPluginDialog/saveDialog/exportDialog(호스트 전용).
- **이벤트**(호스트→페이지): `state`(스냅샷; files는 FilesVersion 변경 시만), `undo`, `status`(경량), `invalidate`(차트 재요청), `frame`(헤더 JSON + 공유 버퍼), `reply`, `test`.
- **프레임 프로토콜**: 페이지가 `{cmd:'frame', chart, w(장치 px), h, dpr, xmin, xmax, commit, fromX0/1, seq}`를 보냄 → 호스트가 풀 스레드에서 FrameServer.Compute → 공유 버퍼에 Float32 기록 → `{ev:'frame', seq, header}`. 최신 요청만 처리(차트별 coalescing). 공유 버퍼를 아직 받지 못한 차트는 base64 인라인(폴백, 자동).
- 파일 드롭: 페이지의 `postMessageWithAdditionalObjects`로 CoreWebView2File.Path 전달.
- 자동화: `window.nx.test.*`가 실제 DOM 이벤트(DragEvent/PointerEvent/WheelEvent)를 발생시키고 결과를 `{cmd:'test'}`로 돌려줌; 캡처는 `CoreWebView2.CapturePreviewAsync`(페이지 1440×900).

## 2. 관문 (WPF 버전 대비)
모든 수치는 app/dist(self-contained) 빌드, 페이지 1440×900, 실제 DOM 이벤트. 프레임 시간 = 페이지의 요청 전송 → 캔버스 그리기 완료(브리지 왕복 + 엔진 계산 + 그리기). 원본 work/webview/dist/{selftest,v3,stress,stress_analysis}/gates.txt.

| 관문 | WebView2 | WPF(이전) | 기준 | 판정 |
|---|---|---|---|---|
| 1 GB 파일 열기 | 6.4 ms | 15 ms | ≤100 | 통과 |
| 10,000채널 파일 열기 / 트리 렌더 | 337 ms / 82 ms | 1821 ms | ≤2000 / ≤500 | 통과 (트리는 가상 스크롤) |
| 10M 첫 곡선 (드롭 → 피라미드 → 첫 프레임) | 347 ms | 1060 ms (앱 경로) | ≤500 | 통과 |
| 10곡선×10M 준비 (DOM 드롭 9회 포함) | 2.2 s | 15.5 s | ≤3 s | 통과 |
| 10곡선×10M 줌 30단계 median / p95 | 5.8 / 9.0 ms | 3.8 / 8.4 ms | ≤16 | 통과 |
| 10곡선×10M 팬 30단계 median / p95 | 5.3 / 6.9 ms | 3.4 ms | ≤16 | 통과 |
| 휠 이벤트 10회 median / 드래그 팬 | 5.7 ms / 6.7 ms | 3.7 ms | ≤16 | 통과 |
| X 매핑 10M(메모리 단조 x) 줌·팬 median | 1.7 / 1.8 ms (raw 구간 1.8) | 2.6 / 2.5 ms | ≤16 | 통과 |
| 커서 클릭 → 판독 표 | A 300 s / B 600 s (클릭·Shift+클릭) | 동일 | — | 통과 |
| 수식 채널 10M / TDMS 저장·재판독 | 462 ms / 100 % 일치 | (1M 기준) / 100 % | =100 % | 통과 |
| REPORT PNG / PDF (풀 스레드로 이동: 10×10M 표 통계가 UI를 4.3 s 막던 것을 수정) | 260–1600 / 296 ms | 356 / 275 ms | — | — |
| 공유 메모리 프레임 | 동작(인라인 폴백 1회 = 첫 프레임) | — | =1 | 통과 |
| 메모리(앱 프로세스 WS, 셀프테스트 끝) | **622 MB** (시작 255, 파일 닫은 뒤 509) | 485 (시작 181) | ≤500 | **미달** — §4 |
| 메모리(WebView2 프로세스 합) | 530 MB (시작 362) | — | — | 기록 |
| v3 VeriStand 기능 관문 20건 | 20/20 PASS (dist) | 20/20 | 전부 | 통과 |
| P3 오프스크린 Skia 렌더(보고서 경로, RenderBenchTests) | full_cursors 10.7 ms (단독) | 14.3 | ≤16 | 통과 |
| 단위 테스트 | 514/514 (최종, 부하 없는 상태; 부하 중에는 P3 full_cursors가 16.2 ms로 1회 실패) | 514/514 | 전부 | 통과 |
| --stress 200회 (모드 4종 순환·DOM 드롭 5·휠·클릭·차트 닫기·파일 재열기, 445 s) | 정지 0 · 최대 정지 0 ms · 예외 0 · 앱 메모리 +10 MB (브라우저 합계 1064 MB, §4) | 정지 0 · +5 MB | 정지 0, ≤100 MB | 통과(앱 프로세스) |
| --stress-analysis 700회 | 타임아웃 0 · 결과 채널 50개 제한 뒤 +39 MB · 정지 0 · 최대 정지 0 ms (시작 직후 WebView2 초기화 정지는 WPF 때처럼 startup 항목으로 분리) | +? | ≤100 MB | 통과 |
| 더블클릭 실행(app/dist, 인자 없음) | 인자 없이 실행 → 런타임 감지 0.9 s, 페이지 준비 1.7 s(trace.log), 창·3패널 표시 확인(작업 관리자 WS 262 MB) | — | — | — |
| WebView2 런타임 | Evergreen 155.0.4283.62 감지(`GetAvailableBrowserVersionString`) → 없으면 `WebView2Runtime\` Fixed Version 폴더 사용, 그것도 없으면 설치 안내 문구 | — | — | 확인 |

## 3. 픽셀 대조 (docs/pixel-diff.md)
세 화면 모두 패널 세로 가장자리 x 좌표 6/6·8/8 일치, 1px 구분선 y 좌표 NAVIGATOR 18/18 · ANALYSIS 17/17 일치, VIEW는 18/18 + 앱이 추가한 PROPERTIES 섹션 하단선 1개. 폰트 크기 집합·패밀리 동일. 남은 차이는 데이터(값·파일명·채널명 축약)와 목업 정적 SVG의 고정 종횡비뿐. 표·사진: docs/pixel-diff.md, work/webview/pixel/*_diff.png.

## 4. 알려진 문제
- **메모리 관문 미달(앱 프로세스)**: WebView2를 WPF 창에 호스팅하는 기본 비용이 +73 MB(시작 255 vs 181)이고, 셀프테스트 끝 622 MB(WPF 485), 파일을 전부 닫아도 509 MB. 프레임 경로(차트당 공유 버퍼 8 MB + 스테이징 8 MB)는 수십 MB. 대체 경로(엔진 PNG 프레임)는 브라우저가 그대로 남으므로 메모리를 줄이지 못해 채택하지 않음. 브라우저 프로세스는 별도 ~530 MB(시작 362).
- 삭제된 차트의 공유 버퍼가 브라우저에 남아 --stress 200회 뒤 브라우저 합계 875 MB → 차트 제거 시 Dispose + `chrome.webview.releaseBuffer`로 수정, 재측정: 앱 프로세스 +10 MB로 유지되지만 브라우저 프로세스 합계는 1064 MB(시작 362) — `chrome.webview.releaseBuffer` 호출 뒤에도 Chromium이 즉시 반환하지 않음(미해결, 창을 닫으면 해제).
- ANALYSIS 결과 채널이 무한 누적(700회 +120 MB) → Analysis/Results 그룹을 마지막 50개로 제한.
- 첫 프레임은 공유 버퍼 도착 전이라 base64 인라인(1회, ~120 ms 포함 워밍업). 이후 모두 공유 메모리.
- 10곡선 노이즈의 포락선은 열(min–max) 막대 α 0.38 + 끝점 1px — 목업에는 없는 경우라 v2 규칙을 따름.
- 디지털 레인·커서 표·범례는 레인 곡선을 제외(목업 "4 curves · 3 lanes").
- 값 테이블(Alt+드롭)은 100행 페이지 넘김(PREV/NEXT)으로 단순화.
- 목업에 없는 상태(차트 선택 속성, 다크 테마, 파일 속성 목록, REPORT)는 같은 토큰으로 구성.
- 자동화는 사용자 layout.json을 쓰지 않음(SelfTest.Enabled → LayoutStore.Save 건너뜀, WebView2 사용자 데이터 폴더도 분리 `WebView2-auto`).

## 5. 사용자 체크리스트
1. 더블클릭 실행(app/dist/NexysView.exe) → 1~2초 뒤 라이트 화면(헤더 알약 탭, 3패널)이 보이는가. 안 보이면 상태 글(WebView2 런타임 없음)과 설치 링크가 떠야 한다.
2. .tdms 파일을 창에 드롭 → 트리에 파일·그룹·채널, 상태줄 "Opened …".
3. 채널을 VIEW 빈 영역에 드롭 → 차트; 차트 위에 드롭 → 곡선 추가; 왼쪽/오른쪽 축 영역 드롭 → 축 지정; 아래 x축 영역에 시간 채널 드롭 → "X · 채널명" 알약 + mm:ss 눈금.
4. 휠 줌·드래그 팬·Shift+드래그 영역 줌·더블클릭 전체·클릭 커서 A·Shift+클릭 커서 B·우클릭 커서 해제; 커서 표에 A/B/Δ.
5. 범례 클릭 숨김, 범례 드래그로 다른 차트에 이동, 헤더 그립 드래그로 차트 교환, Ctrl+Z/Y.
6. 트리 검색 `Sys_Man.*` → DROP ALL (n); TREE/FLAT 전환.
7. NAVIGATOR 모드: 파일 카드, COMPARE n FILES → 공통 채널 체크 + 정렬(START TIME/ZERO/MANUAL) → 채널별 차트.
8. ANALYSIS: 함수 선택 → 파라미터 → RUN → 결과 미리보기 + MIN/MAX/MEAN/RMS + RESULT CHANNELS → SHOW IN VIEW.
9. REPORT: + TITLE/TEXT/CHART/TABLE → PAGE 미리보기 → EXPORT PNG/PDF.
10. THEME(다크), PLOT · LIGHT(점 격자) 전환; 창을 닫고 다시 열면 파일·차트·테마가 복원된다(자동화 실행은 저장 안 함).
