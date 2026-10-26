# Nexys VIEW — v3 보고 (2026-10-22): (A) 라이트 재설계 "Warm Instrument" + (B) VeriStand 로그 기능

문서: design/DESIGN-v3-light.md(토큰·구성 요소) · docs/design-v3-review-1/2/3.md(비교 루프) · 캡처 work/design/v3loop1..3/ · 전후 쌍 work/design/v3pairs/ · 관문 work/bench/gate_v3.txt, gate_tests_v3.txt, gate_p3_v3.txt.
자동화(`--shot/--selftest/--v3/--stress*`)는 사용자 설정·layout.json을 쓰지 않는다. 타사 이름·로고는 UI 어디에도 없다.

## (A) 라이트 재설계

### A1. 토큰 (Tokens.Light.xaml, Tokens.PlotLight.xaml)
| 토큰 | v2 | v3 |
|---|---|---|
| bg / panel / panel-alt | #CFD3D8 / #DDE0E4 / #E7E9EC (쿨 그레이) | **#ECEAE5 / #F7F6F3 / #EFEDE8** (웜 그레이 종이 + 흰 카드) |
| border / border-strong | #9EA4AC / — | **#D9D6CF / #B9B5AC** |
| text / muted / faint | #141618 / #4F555C / #868C94 | #17181A / #6B6A66 / #A6A39C |
| accent | #2E6DB4 (파랑 바·링) | **#17181A 검정 채움**(탭·주 버튼·선택 행, 글자는 bg) |
| accent2 | — | **#E8601C 주황**(커서·스파크라인·포커스 링·범위 막대 현재 점) |
| 반경 | 0 | 패널 10 · 카드 8 · 컨트롤 6 · 알약 999 |
| 플롯(밝음) | #FFFFFF, 선 격자 #D7D7D7, 커서 #141618 | **#FBFAF8, 점 격자 #D9D6CF, 커서 #E8601C**, 플롯 모서리 6 |
| 판독 값 | 모노 20–24px | **Instrument Serif 36px**(패널·분석 카드), 띠는 모노 14px |
| 글꼴 추가 | — | Instrument Serif Regular (OFL, Assets/fonts, LICENSES.txt) |
다크(Tokens.Dark.xaml)는 색 유지 + border-strong #4A4F57, accent2 #E8601C.

### A2. 전후 쌍 6개 (work/design/v3pairs/)
| # | 화면 | before(v2) → after(v3) | 바뀐 것 |
|---|---|---|---|
| 1 | NAVIGATOR 트리 | pair1 | 흰 카드·반경 10, 파일 행 칩 "TDMS · 21 ch", 계층 그룹 Sys_Man/OSM_Out, 선택 행 accent2 띠 + 점 스파크라인, 속성 패널 세리프 판독 + 점 범위 막대 |
| 2 | VIEW 커서 | pair2 | 차트 헤더 36px(아이콘·메타 "2 curves · X System Time · rel"), 커서 상자 시각 형식 "00:12.34", 점 격자·둥근 플롯, 주황 커서 |
| 3 | VIEW VeriStand | pair3 | 계단 곡선(command/Ready/Target Position 자동) + 디지털 레인 3개, 시간 눈금 mm:ss |
| 4 | ANALYSIS | pair4 | 통계 = 세리프 카드 4개(min/max/mean/RMS) + 작은 표, 선택 함수 검정 알약, RUN 주 버튼 |
| 5 | REPORT | pair5 | 종이 카드 반경 10, 뷰포트 폭 맞춤 |
| 6 | NAVIGATOR 모드 | pair6 | 데이터 소스 행 카드(반경 8, 칩 FORMAT/CH/TIME, 숫자 4열 우측 모노) |

### A3. 다크의 구조 변경(색 불변)
반경·알약 탭·칩·세리프 판독·차트 헤더 36px·레인·스파크라인은 공유. 플롯은 어두운 선 격자 그대로(점 격자는 `Plot.GridStyle` 토큰이 "dots"일 때만). 캡처 work/design/v3loop2/v3_13_view_dark.png, example_dark_view.png.

### A4. 남은 차이
- 다크 주 버튼(USE … AS X)은 다크 accent(#D9F23A) 채움 — 색 불변 원칙으로 보류.
- 비교 "start" 정렬은 파일 간격만큼 축이 길어져 짧은 로그가 점으로 보임(의도된 결과, B4 지침 참조).
- 판독 띠는 곡선 3개부터 2줄로 wrap(v2 규칙 유지).

## (B) VeriStand 로그 기능

### B1. 시간 채널을 X축으로
- **사용**: 차트 헤더의 "USE SYSTEM TIME AS X" 알약, 속성 패널 X AXIS 섹션의 USE AS X / 곡선의 SET AS X, 또는 트리에서 시간 채널을 플롯 **아래 x축 영역**에 드롭(미리보기 "Use System Time as X axis"). 후보 = 시간 단위(s/ms/us/min/h)·단조증가·같은 샘플 수(`XIndex.SuggestX`, System Time 우선). 적용은 Undo 가능. 샘플 수가 다른 곡선은 건너뛰고 상태줄에 개수 표시.
- 눈금: 상대(mm:ss.fff) 또는 절대(HH:mm:ss, 날짜는 축 라벨; 1904 epoch UTC, Absolute Time 채널). 속성 패널 "time format" 콤보(absolute/relative/none), CLEAR X CHANNEL.
- 커서 값은 매핑된 x에서 **원본 샘플**을 읽음(`PlotRenderer.ValueAt(Curve,x)` → 이진 탐색 `XIndex.IndexOf`).
- 관문(gate_v3.txt): x_suggested_system_time · x_drop_zone_hint · x_mapped_relative · x_range_is_system_time(121.650..151.750 s) · x_tick_relative_format("00:10") · cursor_value_raw_sample(34.04911766725412 = 원본) · x_absolute_clock_format("05:30:36", 2026-02-27 UTC) · x_undo_restores — PASS.

### B2. 계단 곡선 + 디지털 레인
- 정수/불리언 타입 또는 첫 100k 샘플에 고유값 ≤8인 채널은 드롭 시 자동 **step(sample-and-hold)**; 속성 패널 곡선 "Step" 체크로 전환. 차트 "Digital lanes for step curves"를 켜면 step 곡선을 플롯 아래 16px 0/1 레인으로 적층(라벨 백킹, 채널색 막대).
- 관문: step_auto_detect(command·Ready·Target Position = step, DEG = line) · digital_lanes_3 — PASS.

### B3. 계층 트리 + 와일드카드 검색 + DROP ALL
- 이름에 '.'/'/'가 있는 채널이 3개 이상이면 기본 계층(Sys_Man ▸ ACT_Apply …); 헤더 토글 버튼으로 파일별 flat/계층 전환(레이아웃에 저장).
- 검색: 부분 문자열(대소문자 무시) 또는 `*`/`?` 와일드카드(`Sys_Man.*`, `*ACT*`); 일치 시 부모 자동 펼침, **DROP ALL (n)** 버튼이 일치 채널(≤64)로 새 차트를 만든다.
- 관문: tree_hierarchical_default(Sys_Man.* 8) · search_ACT_matches_ge5(5) · drop_all_curves(5) — PASS.

### B4. 파일 비교 (Compare…)
- 트리 파일 행을 Ctrl-클릭으로 2개 이상 선택 → **COMPARE…** → 공통 채널 체크 + 정렬 모드 → 채널마다 차트 1개, 파일마다 곡선 1개(색 = 파일 순서), 각 곡선은 자기 파일의 시간 채널에 매핑.
- 정렬: **start** = 절대 시작 시각 차이(Absolute Time 첫 값, 없으면 파일 타임스탬프 속성)로 실제 시각 관계 유지 · **zero** = 파일마다 0 s부터 · **manual** = 파일 순서 × 오프셋 ms. 속성 패널 ALIGNMENT 섹션에 파일별 오프셋 표시.
- 지침: 로그 간격이 길면(이 데이터는 1–3시간) start 모드는 긴 축에 짧은 로그가 점으로 보이므로 zero/manual을 쓰고, 동시 기록 로그에서만 start를 쓴다.
- 관문: compare_common_channels(21) · compare_align_start(first-x 0 / +9295.195 / +13106.201 s = 절대 시작 차이) · zero(0/0/0) · manual(0/0.5/1.0 s) — PASS.

### B5. 성능·안정성
| 항목 | 값 | 관문 |
|---|---|---|
| X 매핑 10M 곡선(메모리 단조 x) 줌 30단계 median | 2.6 ms (p95 3.5) | ≤16 |
| 팬 30단계 median | 2.5 ms | ≤16 |
| raw 구간(≤1500 샘플) 팬 20단계 median | 4.0 ms (p95 9.1) | ≤16 |
| SetXChannel 10M | 0.46 ms | — |
| WS (bench1 + 로그 3 + x 80 MB) | 488 MB | ≤500 |
| P3 오프스크린 관문(단독 실행) | full_static 11.9 · pan 8.3 · zoom 10.3 · raw 6.2/3.7 · cursors 14.3 ms | ≤16 |
| 단위 테스트 | 514/514 (V3Tests 11건 추가; 1차 실행의 P2 실패는 아래 폭주 헬퍼 때문, 단독 재실행 통과) | 전부 |
| `--stress --iter 200` | 1차 정지 1(1396 ms, 폭주 헬퍼 중) → 헬퍼 제거·타임아웃 후 dist 재실행 정지 0 · 최대 0 ms · 예외 0 · 메모리 +5 MB (395 s) | 정지 0 |
| dist 재배포 | 265파일 173 MB, dist --selftest 줌 5.6/팬 4.8/휠 5.1 ms, WS 485 MB | — |

### B5-1. 발견한 결함 (수정)
- 감시자 스택 헬퍼 `NexysView.exe --stacks <pid>`(ClrMD 비중단 attach)가 대상이 먼저 종료되면 돌아오지 않음: 18:45에 띄워진 헬퍼가 75분 뒤 **14.8 GB WS·4,100 s CPU**로 발견됨(tasklist). 이 프로세스가 P2/P3 관문 1회 실패와 스트레스 1회 정지(1396 ms)의 원인. 수정: 헬퍼 20 s 자체 종료(수집분 기록 후 Exit 2) + 부모 30 s Kill (Services/Diag.cs).
- 알약 채움 위 글자 소실(ModeTab·Button.Primary·ListBoxItem): 암시적 TextBlock 스타일이 Foreground를 덮음 → ContentPresenter 리소스로 해결 (design-v3-review-1 #3).
- 모드 탭 Storyboard가 템플릿 이름 "bg"를 찾지 못해 시작 시 XamlParseException → 컨트롤 자체 Opacity 애니메이션으로 변경.

### B6. 알려진 한계
- X 매핑은 **같은 샘플 수**의 곡선만(길이가 다른 곡선은 건너뜀). 시간 채널은 단조증가여야 하며 NaN 구간은 미지원.
- 절대 시각은 1904 epoch(TDMS/1904-epoch) 가정; UTC로만 표시(로컬 시간대 변환 없음).
- 계단 자동 감지는 첫 100k 샘플만 본다. 고유값이 9개 이상인 상태 채널은 수동으로 Step을 켠다.
- 비교는 공통 **이름**으로 묶는다(그룹명이 달라도 이름이 같으면 공통).
- 검색 와일드카드는 전체 이름에 앵커(Sys_Man.* 은 "Sys_Man."으로 시작하는 이름만).
- WS 관문이 488 MB로 여유가 12 MB뿐(10M x 배열 80 MB 포함) — 실제 파일의 시간 채널은 mmap이라 더 가볍다.

### B7. 사용자 점검표
1. 로그 열기 → 트리에 Sys_Man/OSM_Out 그룹이 보이는가, 헤더 ≡ 버튼으로 flat 전환되는가.
2. 검색 "ACT" → 5개, DROP ALL (5) → 차트 1개에 5곡선.
3. DEG 드롭 → 헤더 "USE SYSTEM TIME AS X" 클릭 → 축이 mm:ss, 커서 A 값이 원본 값과 같은가(속성 패널 CHANNEL "at cursor A").
4. 속성 time format → absolute → HH:mm:ss + 축 끝 날짜.
5. command/Ready/Target Position 드롭 → 계단, Digital lanes 체크 → 레인 3개.
6. 파일 3개 Ctrl-클릭 → COMPARE… → zero → 3곡선이 0 s에서 시작, manual 500 ms → 0.5 s 간격, start → ALIGNMENT 오프셋이 시각 차이.
7. Ctrl+Z로 X 매핑·레인·비교가 되돌려지는가.
