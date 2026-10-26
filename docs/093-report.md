# Nexys VIEW 0.9.3 beta — VIEW 탐색 도구 집중 (v093, 2026-10-26)

입력: v093.md(동료 피드백). 기반: docs/webview-report.md(구조), docs/beta-report.md(0.9.0~0.9.2), docs/07-ux-catalog.md(기준 도구의 조작 카탈로그 — 오른쪽 드래그 확대, 밴드 커서, 조감도(Bird's eye) 연동을 참고).
관문 원본: work/beta/v093/gates.txt(신규 30건), work/beta/v3_093/gates.txt(회귀 21건), work/beta/selftest_093/gates.txt(회귀), work/bench/gate_tests_093.txt(단위 테스트), work/beta/stress_093/gates.txt(--stress 200).
캡처: work/beta/v093/*.png(관문 캡처), work/beta/shots093/*.png(README·보고서 캡처).

## 1. 바뀐 것 한눈에

| 항목 | 0.9.2 | 0.9.3 |
|---|---|---|
| 상단 탭 | NAVIGATOR / VIEW / ANALYSIS / REPORT | **NAVIGATOR / VIEW** (ANALYSIS·REPORT는 `layout.json`의 `ShowLegacyTabs: true`일 때만; 엔진·명령·셀프테스트는 그대로) |
| 파일/그룹 드롭 | 채널만 드롭 | **파일·그룹·이름 노드 드롭 → 즉시 차트** (BY UNIT / PER CHANNEL, 시간 채널 자동 X, ≤256, 50개 초과는 상태줄 표시) |
| 줌 | 휠·Shift+드래그·더블클릭 | + **오른쪽 드래그 영역 확대**(Shift X만 / Ctrl Y만), **Backspace 줌 되돌리기 20단계** |
| 커서 | A/B | + **밴드 커서**(Ctrl+B): 손잡이·이동·방향키 1/10 px·숫자 입력, 판독 띠 min/max/mean/RMS/p-p |
| 개요/상세 | — | **차트 메뉴 → 상세 차트 연결**, 밴드 ↔ 상세 X범위 양방향 동기, 범례 클릭/드롭으로 상세 채널 교체 |
| 차트 간 | 독립 | **X축 링크**(같은 X채널 또는 같은 파일·단위, 차트별 해제), 링크 차트에 밴드 점선 |
| 축·곡선 | 좌/우, STEP/LINE | **Y축 자동/수동/로그/좌우, 단위별 공유·분리(OWN), 곡선 선/계단/점, 굵기 1·1.5·2, 색 8, 숨김** — 레이아웃 저장 |
| 테이블 | 100행 페이지 | **가상 스크롤(10M)**, 커서 A 행 강조·FOLLOW, 셀 선택 **Ctrl+C 탭 구분 복사**, COPY ALL |
| 내보내기 | 전체 TDMS, 보고서 PNG/PDF | + **밴드 구간(또는 전체) CSV / TDMS**, **차트 PNG 복사·저장** |

## 2. 기능별 사용법 (캡처)

### 2.1 그룹 드롭 → 차트
NAVIGATOR의 파일 행·그룹 행·이름 노드 행을 VIEW의 드롭 존(또는 빈 영역)에 놓는다. 드롭 존 오른쪽의 **BY UNIT**(기본) / **PER CHANNEL**이 규칙이다.
- BY UNIT: 차트 1개, 단위마다 Y축(`PlotModel.AxisFor`), 정수·불리언·값 ≤ 8개 채널은 계단 + **DIGITAL LANES** 자동 on. 파일의 시간 채널(`XIndex.SuggestX`)이 모든 곡선과 길이가 같으면 X축으로 매핑(상대 mm:ss 눈금).
- PER CHANNEL: 채널마다 차트 1개(되돌리기는 첫 차트 한 번에).
- 캡처 work/beta/v093/v093_01_group_drop.png — 로그 파일 "Channel Group 1"(21채널) 드롭: 20곡선(시간 채널은 X), 축 3개(단위 없음·sec·usec), 레인 14. 상태줄 "20 channel(s) → 1 chart(s) · x = System Time".

### 2.2 줌
- 오른쪽 마우스 드래그: 점선 사각 미리보기 → 놓으면 X범위 + Y범위(모든 Y축을 수동 범위로) 확대. `Shift`+오른쪽 = X만, `Ctrl`+오른쪽 = Y만. 왼쪽 `Shift`+드래그(X만)는 그대로.
- 휠 = 커서 기준 줌, 더블클릭 = 전체(Y 자동 복귀), **Backspace** = 직전 뷰(X + Y범위)로, 20단계(`PlotModel.ZoomHistory`; 휠·팬·영역 확대·전체 보기가 모두 기록).
- 캡처 work/beta/v093/v093_02_right_zoom.png (25–75 % 영역 확대 뒤).

### 2.3 밴드 커서와 판독 띠
- `Ctrl+B` 또는 차트 헤더의 **A / B** 알약 → BAND. 처음 켜면 화면 40–60 %에 밴드가 생긴다. 가장자리 손잡이(8×28 px) 드래그 = 폭, 안쪽 드래그 = 이동, `←` `→` = 1 px, `Shift`+`←` `→` = 10 px, PROPERTIES의 BAND 시작·끝 입력(Enter) = 정확한 x.
- 판독 띠(커서 표 자리): BAND 시작 → 끝 · 길이, 곡선별 MIN / MAX / MEAN / RMS / P-P. 드래그 중에는 통계 계산을 건너뛰고(`BandLive`) 놓는 순간 계산한다. 구간이 65,536 샘플을 넘으면 mean·RMS는 ≤ 2,048 표본의 추정치(≈ 표시), min/max는 피라미드로 정확.
- 캡처 work/beta/v093/v093_03_band.png, work/beta/shots093/log_detail.png.

### 2.4 개요/상세 연동 (핵심)
![개요/상세](../work/beta/shots093/readme_detail.png)
- 차트 메뉴(⋯) → **상세 차트 연결**(또는 PROPERTIES → LINK DETAIL). 개요에 밴드가 없으면 만들고, 바로 아래에 상세 차트(`Chart.DetailOf = 개요 id`, 제목 "Detail · 채널")가 생긴다. 상세는 개요에서 선택한 채널 1개(선택 채널 없으면 첫 곡선)를 밴드 구간만 보여준다. X 매핑(시간 채널·상대/절대 모드)은 개요와 같다.
- 밴드 이동·폭 변경 → `Engine.SetBand` → `SyncDetail`: 상세의 XMin/XMax = 밴드(샘플 오차 0, 관문 detail_follows_band Δsamples=0.000). 상세에서 줌·팬 → `Engine.AfterView`: 개요 밴드 = 상세 X범위(관문 band_follows_detail_zoom).
- 상세 채널 바꾸기: 개요 범례 클릭(상세가 연결된 개요에서는 숨김 대신 선택, `Shift`+클릭 = 숨김), PROPERTIES → SHOW IN DETAIL, 상세 차트에 채널 드롭(= 교체, 관문 detail_drop_replaces_channel).
- 캡처 work/beta/v093/v093_04_detail.png, v093_06_overview_detail_10M.png(10곡선 × 10M 개요 + 상세), shots093/readme_detail.png(샘플).

### 2.5 X축 링크
- 링크 키 = 매핑된 X채널 id, 없으면 첫 곡선의 파일 + x 단위(`Engine.LinkKey`). 같은 키의 차트는 한 차트의 팬·줌(프레임 요청마다)이 즉시 전파된다(`PropagateLink`; 페이지는 `invalidate`에 프레임 헤더의 x 창을 채택). 상세 차트는 링크하지 않는다.
- 헤더 **LINK** 알약(또는 PROPERTIES LINK X)으로 차트별 해제. 개요의 밴드는 같은 그룹의 차트에 연한 점선(`bandRef`)으로 보인다.

### 2.6 축·곡선 (PROPERTIES)
- Y AXES 절: 축마다 단위·곡선 수, AUTO / MANUAL(MIN·MAX 입력), LOG(0 이하 건너뜀, 10의 거듭제곱 눈금·2 decade 이하면 1-2-5), LEFT / RIGHT.
- 곡선: LINE / STEP / POINTS, WIDTH 1·1.5·2, AXIS L/R, SHARED(같은 단위 축 공유) / OWN(이 곡선만의 축), HIDE/SHOW, 색 8칸. GRID on/off.
- 저장: `LayoutStore.PlotDto`에 CursorMode·Band·LinkX·DetailOfIndex·Axes(id·unit·right·auto·min·max·log), `CurveDto`에 Width·Points·AxisId.

### 2.7 값 테이블
![값 테이블](../work/beta/shots093/readme_table.png)
- 드롭 존 **+ TABLE**(활성 차트의 채널) 또는 `Alt`+드롭 또는 차트 메뉴 **+ TABLE**. 열 = # · x(활성 차트의 X 매핑) · 채널. 가상 스크롤: 200행 블록을 `table.rows`로 받아 LRU 40블록, 10M행은 스페이서 6M px로 비율 스크롤.
- 활성 차트의 커서 A(없으면 밴드 시작) 행이 강조되고 FOLLOW면 가운데로 스크롤(스냅샷 `cursorRow`). 셀 드래그 선택 → `Ctrl+C` = 탭 구분(헤더 포함, ≤ 100,000행) → 호스트 클립보드. COPY ALL = 보이는 열 전체.

### 2.8 내보내기
- 차트 메뉴 / PROPERTIES EXPORT: **CSV…**(index, x, 채널…; 밴드가 있으면 밴드 구간, 없으면 전체; `Export/CsvExport`), **TDMS…**(`TdmsExport.SaveRange`, 구간의 wf_start_offset 보정, `nexys_export_first_sample` 속성), **PNG COPY / PNG…**(캔버스 PNG → WPF 클립보드/파일). 모든 채널 TDMS는 그대로 `Ctrl+S`.

### 2.9 탭 정리
- `Engine.ShowLegacyTabs`(layout.json `ShowLegacyTabs`)가 false면 헤더 탭·`Ctrl+3/4`·도움말의 분석/보고서 절이 빠지고 시작 모드가 ANALYSIS/REPORT였으면 VIEW로. 엔진(AnalysisService/ReportService)·브리지 명령·`--selftest`의 ANALYSIS/REPORT 단계는 그대로 동작한다(회귀 통과). 통계는 밴드 판독 띠로, 내보내기는 CSV/TDMS/PNG로 흡수.

## 3. 관문 수치 (work/beta/v093/gates.txt, 2026-10-26, bin Release, 페이지 1440×900)

| 관문 | 결과 | 기준 |
|---|---|---|
| tabs_navigator_view_only | PASS (NAVIGATOR,VIEW) | — |
| group_drop_chart / x_time / axes_by_unit / lanes / per_channel | PASS (20곡선, x=System Time relative, 축 3, 레인 14, 채널마다 +20) | — |
| right_drag_zoom_box / x_only / y_only | PASS (x 25–75 % 정확, Y 수동 / X만 / Y만) | — |
| backspace_zoom_back / to_full | PASS (히스토리 3→2, Y 복원, 전체 복귀·Y 자동) | — |
| band_mode_ctrl_b / drag_moves / handle_resizes / arrow_nudge_10px / readout_strip | PASS (이동 3.010 = 기대 3.010, 폭 7.525 = 기대, 10.00 px) | — |
| detail_chart_created / detail_follows_band / band_follows_detail_zoom / detail_drop_replaces_channel | PASS (Δsamples = 0.000) | — |
| link_key_shared / link_pan_follows / link_off_independent / band_ref_on_linked_chart | PASS | — |
| table_cursor_row_777 / table_copy_tsv_values | PASS (cursorRow 777 표시·스크롤, TSV 값 = 엔진 값) | — |
| csv_band_export_values / tdms_band_export_values | PASS (495행 = 기대 495, 첫 행 index·x·값 일치, TDMS 재판독 495 샘플) | — |
| chart_png_saved | PASS (14 KB) | — |
| overview_10x10M_ready_ms | 187 ms | ≤ 3000 |
| **band_drag_live_detail_frames median** | **11.8 ms** (첫 구현 37.9 → 통계 생략·병렬 프레임 13.9 → 호스트 직접 재프레임 11.8) | ≤ 16 |
| **band_drag_live_overview_frames median** | **9.4 ms** | ≤ 16 |
| band_nudge_commit_detail_frames median (방향키 커밋, 통계 포함) | 정보 (gates.txt) | ≤ 60 |
| overview_pan_30 median / detail_zoom_30 median | 6.6 / 4.0 ms | ≤ 16 |
| detail_sync_after_zoom | PASS | — |
| memory_ws_mb (10×10M 개요+상세, 밴드·줌 뒤, GC 후) | 642.6 MB (private ≈ 296 MB) | ≤ 700 (이전 수준: selftest 끝 622 MB) |
| frames_shared_buffer | 1 | = 1 |

성능 조치: ① 드래그 중 밴드 통계 생략(`BandLive`) ② 차트별 `FrameServer`(개요와 상세 프레임 병렬 계산; 이전엔 하나의 락) ③ 밴드 메시지를 애니메이션 프레임 대기 없이 즉시 전송(2 ms 병합) ④ 호스트가 `band` 명령을 받으면 invalidate 왕복 없이 개요·상세 프레임을 직접 계산(`Bridge.Reframe`, 마지막 요청 뷰포트 재사용) → 상세 median 37.9 → 16.5 → 13.9 → **11.8 ms**. 밴드 통계 표본 4096 → 2048으로 메모리 맵 페이지 상주 감소(WS 716 → 643 MB).

### 회귀 (같은 빌드)
| 묶음 | 결과 |
|---|---|
| `--v3` 시간축 로그 관문 | 21/21 PASS, fail=0 (work/beta/v3_093) |
| `--selftest` | 줌 30단계 4.8 ms · 팬 4.5 · 휠 4.4 (≤16), 공유 버퍼 1, 메모리 590.7 MB (관문 500 미달은 WebView2 호스팅 기본 비용으로 0.9.0부터 기록된 사항, 0.9.2 622 → 590) (work/beta/selftest_093) |
| 단위 테스트 | 514/514 (work/bench/gate_tests_093.txt) |
| `--stress --iter 200` (NAVIGATOR/VIEW 순환, 드롭 1000·차트 닫기 200·파일 재열기) | 예외 0 · UI 정지(300 ms) 0 · 최대 정지 0 ms · 앱 메모리 +26 MB · WebView2 516 MB · 425 s (work/beta/stress_093/gates.txt) |

## 3.1 배포
- `app/dist/NexysView-0.9.3-beta-setup.exe` 61.8 MB (61,786,571 B), `app/dist/NexysView-0.9.3-beta-portable.exe` 82.2 MB (82,186,307 B). 무인 설치(`/VERYSILENT /CURRENTUSER`, work/beta/install_test/0.9.3) → 설치본 `--fresh` 실행(제목 "Nexys VIEW 0.9.3 beta") → 무인 언인스톨(폴더 제거) 확인. 포터블 `--fresh` 실행 제목 확인.
- 저장소 https://github.com/Kim-Hakseong/nexys-view 커밋 5bd34e9, 태그 v0.9.3-beta, Release https://github.com/Kim-Hakseong/nexys-view/releases/tag/v0.9.3-beta (두 파일 첨부). 내보내기 검사: 타사 이름 0건, 바이너리 0건. README는 ANALYSIS/REPORT 절을 빼고 VIEW·PROPERTIES·값 테이블·내보내기 절과 단축키 표를 새로 썼다(캡처 docs/images/detail.png, table.png).

## 4. 구현 메모
- 페이지는 프레임 헤더의 x 창을 채택한다(`PlotView.request({adopt})` — invalidate/resize/링크 전파 때 로컬 xmin/xmax를 보내지 않음). 이전에는 invalidate 뒤 요청이 페이지의 옛 창을 엔진에 다시 써서 ZoomFull·링크가 되돌아갈 수 있었다.
- 밴드 드래그: 페이지가 로컬 밴드를 그리며 `{cmd:'band'}`를 즉시 전송(2 ms 병합) → `Engine.SetBand(record:false)` → 개요·상세 `ChartChanged` → 두 프레임 병렬. 놓으면 `commit:true`(되돌리기 1건, 스냅샷 1번).
- 오른쪽 드래그 줌은 `zoomBox {x0,x1,fy0,fy1}`(fy = 플롯 위에서의 비율) → 모든 Y축을 수동 범위로(로그 축은 로그 공간에서 보간).
- 드롭 규칙은 `Engine.DropGroup`; 트리의 파일/그룹/노드 행은 `draggable`이고 `application/x-nexys-group` 타입을 붙여 드롭 존이 구분한다. 노드 아래 채널은 접힌 자식까지 파일 데이터에서 모은다.
- 테이블 x 열은 활성 차트의 곡선 X 매핑(`Curve.XAt`)을 쓴다(커서 행 계산과 같은 기준).

## 5. 알려진 제한
- 밴드 mean/RMS는 큰 구간에서 표본 추정(≈). 정확한 값이 필요하면 CSV 내보내기.
- 메모리 WS는 메모리 맵 파일 페이지를 포함한다(밴드·상세가 원본 샘플을 읽을수록 커지고 OS가 회수). 힙(private)은 ~300 MB.
- X축 링크 키는 "같은 X채널" 또는 "같은 파일 + x 단위"다. 다른 파일의 로그는 COMPARE(시작 시각 정렬)로 겹친다.
- 로그 축: 0 이하 값은 건너뛰고, 모두 0 이하면 선형처럼 보인다.
- 상세 차트는 1곡선. 개요를 닫으면 상세는 보통 차트로 남는다.
- `--stress`는 모드 2개(NAVIGATOR/VIEW)만 순환한다(숨긴 탭은 제외).
