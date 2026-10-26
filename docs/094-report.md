# Nexys VIEW 0.9.4 beta — 강조 제거 · 드래그 미리보기 버그 · 툴바 통합 · 페이지와 여러 창 (v094, 2026-10-26)

입력: v094.md. 기반: docs/093-report.md. 관문 원본: work/beta/v094/gates.txt(신규), work/beta/v093, v3_094, selftest_094(회귀), work/bench/gate_tests_094.txt, work/beta/stress_094/gates.txt(--stress 200 + 창 50회). 캡처: work/beta/v094/*.png.

## 1. 카드 포인트 효과 제거 목록

기준: accent 계열(`--accent2` #E8601C, `--accent-line`, `--accent`)을 **테두리·좌측 띠·outline**으로 쓰는 요소. 채움(활성 알약 배경), 글자색(Δ·밴드 길이), 밴드 영역·손잡이, 진행 막대, 메뉴 체크 아이콘은 "테두리 강조"가 아니므로 유지.

| # | 요소 | 전 | 후 |
|---|---|---|---|
| 1 | 상세 차트 카드 `.chart.detail` | 좌측 3px 주황 띠 | 띠 없음. 헤더에 회색 칩 **linked · Chart 1**(`.chart-h .chip`, panel-alt 배경) + 개요의 밴드 색 |
| 2 | 사용 가이드 안내 상자 `.help-note` | 좌측 3px 주황 띠 + 배경 상자 | 일반 문단(muted 글자) |
| 3 | 차트 드롭 미리보기 `.plot-box .dropfx` | 1.5px 주황 점선 + 주황 글자 | 1px `--plot-axis` 점선, 회색 반투명 |
| 4 | 드롭 존 hover `.dropzone.over` | 주황 테두리·글자 | border-strong + panel-alt 배경 + 굵기 600 |
| 5 | 활성 차트 `.chart.active` | 카드 테두리 border-strong | 헤더 배경 panel-alt |
| 6 | 차트/테이블/파라미터 드래그 대상(`style.borderColor = accent2`, plot.ts·view.ts·analysis.ts) | 주황 테두리 | `.over` 클래스 → panel-alt 배경 |
| 7 | 영역 확대 사각 `.zoombox` | 주황 점선 | `--plot-cursor` 점선, 회색 반투명 |
| 8 | 색 견본 선택 `.swatches .sw.on` | accent-line outline | border-strong outline |
| 9 | 테이블 선택 셀 `td.sel` / 커서 행 `tr.cur` | 주황 반투명 + 주황 inset 선 | `--band` 톤 배경 / panel-alt 배경 + 굵기 600 |
| 10 | 테이블 포커스 `.tbl:focus` | accent-line outline | border-strong inset 1px |
| 11 | 내비게이터 XY 선택 행(`picked`, 인라인 outline accent2) | 주황 outline | `.picked` 클래스 → panel-alt 배경 + 굵기 600 (§2 버그와 같은 자리) |
| 12 | 투어 스포트라이트 링 `.tour-spot` | 1.5px 주황 링 | border-strong 링 |
| 13 | 키보드 포커스 링 `:focus-visible` | accent-line outline | border-strong outline |
| 14 | 모드 알약 그룹 `.modes a.on` | accent 채움 | 툴바 버튼으로 대체(§3) |

검증: `nx.test.accentScan()`이 `.card .chart .chart-h .tbl .help-note .dropfx .dropzone .ds-card .row-ch .row-file .ptab .tool .pill .menu .drag-preview .tour-card .tour-spot`의 computed border/outline 색을 accent 3색과 대조 — 관문 `accent_borders_zero` **0건**(밴드·상세·도움말을 띄운 상태).

## 2. 버그: 다중 선택 드래그 미리보기 위치

- 증상: 채널 2개를 Ctrl-클릭으로 고른 뒤 드래그하면 채널명 묶음이 NAVIGATOR 검색창 아래 엉뚱한 자리에 겹쳐 보였다.
- 원인(확정, navigator.ts `rowHtml`): 선택 강조를 위해 행 `<div>`에 `style="outline:…"`을 붙이고 **같은 요소에 두 번째 `style="position:absolute;top:…px"`**을 또 붙였다. HTML 파서는 중복 속성 중 첫 번째만 받아들이므로 선택된 두 행은 **절대 위치(top)를 잃고 가상 스크롤 컨테이너의 맨 위(검색창 바로 아래)에 일반 흐름으로 쌓였고**, 브라우저가 그 위치의 행을 드래그 고스트로 그렸다. 즉 "좌표 기준 컨테이너(가상 스크롤 `.inner`)의 고정 오프셋(`top`)이 선택 행에서만 사라진" 것.
- 수정: 선택 표시를 `.picked` 클래스로 옮겨 style 중복을 없앴다(관문 `picked_rows_keep_absolute_position`: 두 행 모두 position:absolute, top 85/112). 드래그 미리보기는 OS 고스트 대신 **중립 카드**(`.drag-preview`, panel-alt 배경, 테두리 없음, "n channels" + 첫 3개 이름)를 만들어 `document`의 dragover 좌표(+14 px)를 따라가며 창 안으로 clamp한다(`setDragImage`에 투명 1×1). 관문: 시작 시 표시, (300,400) → 카드 (314,414), 창 모서리에서 right ≤ vw·bottom ≤ vh, dragend에 제거. 캡처 work/beta/v094/v094_05_drag_preview.png.

## 3. 툴바 통합

- 헤더 = 브랜드 + 툴바 한 줄. 툴바 첫 그룹 `.tools-modes`에 **NAVIGATOR**(list 아이콘) / **VIEW**(chart-line) 버튼이 OPEN·SAVE와 같은 `.tool`(아이콘+라벨) 스타일로 들어가고 뒤에 1px 구분선. 활성 = `panel-alt` 배경 + 하단 2px `--accent-line` 선 + 굵기 600(관문 `toolbar_active_style`), 비활성은 다른 툴바 버튼과 같다.
- DEV 버튼 제거, `Ctrl+Shift+D`로만 토글; 켜지면 상태줄 오른쪽에 "DEVELOPER ·" 표시(헤더의 "· DEVELOPER"는 삭제).
- 캡처 work/beta/v094/v094_01_toolbar_light.png, v094_02_toolbar_dark.png.

## 4. 다중창 + 페이지

### 구조
```
주 창 MainWindow (WPF)                           떼어낸 창 ChartWindow #2 (WPF)
┌─────────────────────────────────────┐        ┌─────────────────────────────────────┐
│ WebView2  index.html                 │        │ WebView2  index.html?win=2           │
│  · 페이지 띠(Page 1·2…, GRID, AUTO)   │        │  · 헤더: WINDOW 2 · n charts · RETURN ALL│
│  · 차트 = windowId 0 & pageId 활성    │        │  · 차트 = windowId 2 (페이지 무관)        │
│ Bridge #1 ──┐                         │        │ Bridge #2 ──┐                         │
└─────────────┼─────────────────────────┘        └─────────────┼─────────────────────────┘
              │  Engine.Changed / ChartChanged → 모든 Bridge가 구독 (= 창 간 브로드캐스트)
              ▼                                               ▼
        ┌───────────────────────── Engine (하나) ─────────────────────────┐
        │ Charts[].PageId / WindowId · Pages[] · ActivePage · AutoPage(N) │
        │ 밴드·X링크·개요/상세·커서는 모델 공유 → 어느 창에서 바꿔도 같은 모델  │
        │ 프레임: 브리지마다 공유 버퍼 + FrameServer(차트별) → 창마다 그리기 │
        └───────────────────────────────────────────────────────────────┘
```
- `detachChart {chart, x?, y?}`(호스트) → `ChartWindow(id)` 생성(같은 `CoreWebView2Environment`, 같은 사용자 데이터 폴더) → `Engine.DetachChart`: 차트(+그 상세 차트)의 `WindowId = id`. 페이지는 자기 창 id의 차트만 그린다(`main.ts`의 `WIN` 필터).
- 떼어내기 경로: 차트 메뉴 "새 창으로", 또는 헤더 그립을 **창 밖으로 드래그**(dragend에서 dropEffect none + 포인터가 뷰포트 밖 → 그 화면 좌표에 창). 관문 `grip_drag_out_opens_window`.
- 동기: 밴드 드래그(주 창) → `Engine.SetBand` → `ChartChanged(상세)` → 창 2의 Bridge가 invalidate → 창 2가 프레임 요청(관문 `band_sync_across_windows`: 창 2 페이지의 상세 xmin = 밴드, 프레임 증가). X축 링크·커서도 같은 경로.
- 창 닫기(사용자 ×) → `OnChartWindowClosing` → `Engine.CloseWindow`: 창의 차트 전부 `WindowId = 0`(삭제 아님). RETURN ALL / 마지막 차트 "주 창으로" → 창 자동 닫힘. 관문 `close_window_returns_chart`, `windows_left_open = 0`.
- 창 위치·크기: `LayoutStore.Dto.Windows[{Id,X,Y,W,H}]`에 저장(닫을 때 기록), 같은 id의 창을 다시 열 때 재사용. `ChartWindow.OnAScreen`이 가상 화면과 겹치는지 확인해 없으면 주 창 기준 캐스케이드/주 모니터. 주 창 rect도 같은 검사로 복원.
- 페이지: `Engine.Pages`(Id·Name·Columns), `ActivePage`, `AutoPage`/`AutoPageN`. `AddChart`가 활성 페이지에 넣고 N개를 넘으면 새 페이지를 만든다(상세 차트는 개요 페이지에 고정). 탭 UI(view.ts): 클릭 선택, 더블클릭 이름(Enter/Esc), 드래그 순서(`page.moveTo`), × 제거(차트가 있으면 모달 확인, 차트는 첫 페이지로 이동), `+`. GRID 1 COL / 2 COL(`.charts` CSS grid), AUTO PAGE ON/OFF + n 입력. `Ctrl+PageDown/PageUp`. 레이아웃에 페이지 목록·활성·AUTO 저장.

### 관문 (work/beta/v094/gates.txt)
| 관문 | 결과 |
|---|---|
| toolbar_modes_first_group / toolbar_active_style | PASS (NAVIGATOR, VIEW* · inset 2px 선 · 600) |
| picked_rows_keep_absolute_position | PASS (top 85 / 112, 둘 다 absolute) |
| drag_preview_shown / follows_pointer / stays_in_window / removed_on_end | PASS ((300,400) → (314,414); 모서리에서 right 1439.6 ≤ 1440, bottom 899.5 ≤ 900) |
| accent_borders_zero | PASS (0건) |
| detail_link_chip | PASS ("linked · Chart 1") |
| detach_opens_window / detached_chart_moves_between_pages / band_sync_across_windows / close_window_returns_chart / grip_drag_out_opens_window | PASS (창 2, 주 창에서 사라지고 창 2에 표시, 밴드 8.938 = 상세 8.938 = 창 2 xmin, 닫으면 복귀) |
| page_add_selects / chart_belongs_to_active_page / page_tab_switches / page_rename / page_reorder / grid_two_columns / page_remove_confirms_and_moves_charts / auto_page_split | PASS (n=2에서 3개 추가 → 페이지 1→3) |
| memory_ws_per_window_mb / memory_webview_per_window_mb | 10.0 MB / 97.4 MB (창 1개 추가, 10곡선 × 10M 차트 포함; 첫 실행 13.4 / 98.5) |
| **cross_window_pan_latency median** (창 2개 각 10곡선 × 10M, 주 창 팬 30단계 → 창 2 프레임) | **4.3 ms** (첫 실행 5.9 / p95 11 / max 16.5) (gate ≤ 16) |
| cross_window_pan_synced | PASS (30/30 측정, 두 창 XMin 일치) |
| memory_ws_mb (끝) | 383.7 MB (≤ 700) |
| windows_left_open / frames_shared_buffer | 0 / 1 |

측정 방법: 주 창 페이지가 팬 요청을 보낸 시각(`performance.timeOrigin + now`)과 창 2 페이지가 같은 차트(X 링크)의 프레임을 그린 시각(`paintEpochs`)의 차 — 두 WebView2 프로세스의 epoch 시계로 창 간 지연을 직접 잰다.

### 회귀 (같은 빌드)
| 묶음 | 결과 |
|---|---|
| `--v093` (0.9.3 기능 30건) | 30/30 PASS, fail=0 (work/beta/v093) — 툴바 선택자와 PER CHANNEL 드롭의 자동 페이지 나눔에 맞춰 관문 코드 2곳 수정 |
| `--v3` | 21/21 PASS, fail=0 (work/beta/v3_094) |
| `--selftest` | 줌 30단계 5.1 ms · 팬 4.7 · 휠 4.8 (≤16), 공유 버퍼 1, 메모리 592 MB(기록) (work/beta/selftest_094) |
| 단위 테스트 | 514/514 (work/bench/gate_tests_094.txt) |
| `--stress --iter 200` + 창 열기/닫기 50회 | §6 |

## 5. 알려진 제한
- 떼어낸 창에는 NAVIGATOR가 없다(채널 드롭 불가). 창 간 차트 드래그는 없고, 되돌리려면 창을 닫거나 메뉴 "주 창으로".
- 창마다 WebView2 렌더러가 하나 더 떠 약 100 MB가 든다(닫으면 해제).
- 테이블은 주 창 페이지에만 있다.
- 창 위치 복원은 "같은 창 번호를 다시 열 때" 적용된다(차트 자체는 이전 세션에서 복원하지 않는 정책이 그대로라, 시작 시 빈 창을 다시 띄우지는 않는다).

## 6. --stress 200회 + 창 50회
- 첫 실행에서 `stress_max_ui_stall_ms 5646`이 나와 추적: 스택 덤프(work/hang/20261026_172519_798.txt)의 UI 스레드는 `SelfTest.Stress` 상태 기계 안의 `Task.InternalWaitCore` — 창 수 측정용으로 새로 넣은 `WebViewProcessCount()`가 msedgewebview2 프로세스마다 WMI 명령줄 질의를 `GetAwaiter().GetResult()`로 **UI 스레드에서 동기 대기**(6.3 s). 앱 코드가 아니라 측정 코드의 정지였고, 같은 패턴의 `WebViewMemoryMb()`도 마지막 관문 뒤에 8 s를 막고 있었다. 둘 다 `Task.Run` + `await`로 바꿈(모든 셀프테스트 호출부).
- 같은 추적에서 창을 닫은 뒤에도 그 창의 Bridge가 엔진 이벤트를 구독한 채 폐기된 WebView2에 메시지를 보내던 문제(트레이스 "post failed … disposed" 다수)를 발견 → `Bridge.Dispose()`(이벤트 해제·공유 버퍼 해제)를 `ChartWindow.Closed`에서 호출.
- 창 열기 비용(트레이스): CoreWebView2 준비 72–83 ms, 닫기 20–32 ms.
- 결과(work/beta/stress_094/gates.txt): 200회 · 드롭 1000 · 차트 닫기 200 · **창 열기/닫기 50회** · 예외 0 · UI 정지(300 ms) 0 · 최대 정지 **0 ms** · 앱 메모리 +31 MB · WebView2 526 MB · 잔여 창 0 · WebView2 프로세스 증감 0 · 467 s.
