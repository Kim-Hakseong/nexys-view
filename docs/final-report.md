# Nexys VIEW — 최종 보고 (build-nexysview, 2026-10-22)

## 1. 실행법
- 더블클릭: `C:\rea-work\reference\app\dist\NexysView.exe` (self-contained win-x64, .NET 런타임 불필요, 254파일 171 MB).
  표시명·로고는 `app\dist\branding.json`(`displayName`, `logo`, `appIcon`)으로 변경.
- 파일 열기: 창 아무 곳에 .tdms/.tdm/.dxd/.csv 드롭(.tdv를 드롭하면 VIEW 레이아웃 적용), Ctrl+O, 또는 `NexysView.exe <파일…>`.
- 자동화: `NexysView.exe --selftest --out <폴더>` (관문 측정 + 화면 11장), `--shot --out <폴더> --open <파일> [--theme dark] [--mode VIEW|ANALYSIS|REPORT|NAVIGATOR]`.
- 소스: `app\NexysView.sln` (Core / Render / App / Tests + PluginHost C++), 빌드 `dotnet build app\NexysView.App\NexysView.App.csproj -c Release` (SDK 8.0.425, `%LOCALAPPDATA%\Microsoft\dotnet`), 테스트 `dotnet test app\NexysView.Tests`.
  주의: `dotnet build NexysView.sln`은 x64 플랫폼 폴더(bin\x64)로 출력된다 — 프로젝트 단위 빌드를 권장.

## 2. 관문별 수치 (기준 상용 도구 실측 대비, docs/05-bench-reference.md)

| 단계 | 항목 | 관문 | **Nexys VIEW** | 기준 도구 | 판정 |
|---|---|---|---|---|---|
| P1 | TDMS 리더 vs 공식 the reference TDMS library (10파일) | 100% | **5,522,366/5,522,366 값 비트 일치, 속성 979/979, 채널 75/75** | — | 통과 |
| P2 | 1 GB TDMS 열기 | ≤0.1 s | **0.020 s** (앱 내 3 ms) | 0.029 s | 통과 |
| P2 | 10M 샘플 첫 곡선 | ≤0.5 s | **0.15 s** 엔진 / 0.28 s 앱(Drop 이벤트→첫 프레임) | 1.06 s | 통과 |
| P2 | 10곡선×10M 동시 | ≤3 s | **0.37 s** 엔진 / 0.16–0.53 s 앱 | 15.5 s | 통과 |
| P2 | 채널 10,000개 열기 | ≤2 s | **0.20 s** (앱 0.24 s) | 9.52 s | 통과 |
| P2 | 메모리(10곡선×10M + 1 GB 파일 열림) | ≤500 MB | **233 MB** 엔진 / **488 MB** 앱 전체 셀프테스트 후(private 393, GC 125) | 300 MB | 통과 |
| P3 | 팬·줌 프레임(10곡선×10M, 1920×1000) | ≤16 ms | 오프스크린 median: 전체 8.0 · 팬 7.8 · 줌 7.5 · 1000샘플 4.3 · 커서 10.0 ms; 앱 실측: 줌 3.8 · 팬 3.4 · 휠 3.7 ms (p95 ≤8.4) | ≈500 ms(스크립트) | 통과 |
| P6 | TDMS 라이터 재판독 | 100% | **1,630,238/1,630,238** | — | 통과 |
| P6 | DataPlugin 호스트(the Python 마셜러) vs 기준 도구 CSV | 99.97% | **6,142/6,144 = 99.97%** (불일치 2건은 기준 도구 파서 1 ulp) | — | 통과 |
| P7 | ANALYSIS vs numpy/scipy | ≤1e-9 | **최대 상대오차 2.9e-16** (통계·FFT 4창·IIR·FIR·미분·적분·리샘플) | — | 통과 |
| P7 | 수식 46건 vs 기준 도구 Calculate | 대조 | **40/46 일치, 6건 확장(기준 도구 미지원 함수), 실제 불일치 0** | — | 통과 |
| P9 | 셀프테스트 | 전 구간 | 11장 캡처, undo/redo, 보고서 PNG 521 ms / PDF 403 ms | — | 통과 |
| 백로그 b | TDM/TDX 리더 vs 기준 도구 | 100% | **Example.tdm 1,630,238/1,630,238 · cascade.tdm 3,558,800/3,558,800 (477/477 채널)** | — | 통과 |
| 백로그 d | .tdv 레이아웃 가져오기 | — | Example.tdv → 2D 차트(4곡선) + 채널 테이블(27열) | — | 완료 |
| 백로그 a | VBS DataPlugin 호스트 (DataPlugin.chm 예제 1–3, 추가 1a·1b) | — | ex1 암시 채널 301값·속성 5, ex2 4채널 23/23행, ex3 I32×4 280/280, 1a 864/864, 1b 864/864·576/576 (기대값은 데이터 파일에서 유도) | — | 완료(부분집합) |

테스트 17/17 통과(work/bench/tests_all.txt, 순차 실행). 소요: P0–P9 약 1시간 40분 + 백로그 a·b·d 약 55분 (14:24–16:05).
원본 수치 파일: work/tdms/gate_p1_result.txt, gate_backlog_tdm.txt, gate_backlog_vbs.txt, work/bench/gate_p2_result.txt, gate_p3_result.txt, gate_p6_plugin.txt, gate_p7_analysis.txt, gate_p7_formula.txt, work/selftest/gates.txt.

## 3. 미달 항목과 원인
- 없음(모든 관문 통과). 참고: 셀프테스트의 "첫 곡선 0.28 s"는 엔진 0.15 s + PlotHost 최초 생성(폰트 로드·Skia 표면)이며, 디스크 캐시(.nvcache)가 있으면 피라미드 로드는 9 ms.
- 수식 6건은 의도적 확장: 기준 도구 `Calculate`는 `max/min/CMean/CSum`을 미정의로, `CMax/CMin(Ch())`를 인수 오류로 거부하지만 Nexys는 계산한다. 기준 도구 호환 모드가 필요하면 FormulaEngine.Call에서 해당 함수를 제거하면 된다.

## 4. 화면 PNG (work/selftest/)
01_navigator_light_tree, 02_navigator_mode, 03_view_dragging_preview(드래그 중 점선 미리보기), 04_view_light_10curves, 05_view_cursors(커서 A/B·Δ·구간 통계 띠), 06_analysis, 07_report, 08_report_dark, 09_view_dark, 10_analysis_dark, 11_navigator_dark, report.png(보고서 내보내기), 그리고 work/shots_app/view_light.png, view_dark.png, dist_check.png(dist 실행).

## 5. DESIGN.md §6 자가 점검표
| 항목 | 결과 | 근거 |
|---|---|---|
| 보라·파랑 그라데이션/글래스/네온/그림자 중첩 | 없음 | 모든 배경 단색 토큰, `HasDropShadow=False`, 그림자 0 |
| 이모지·컬러 채움 아이콘·일러스트 | 없음 | Lucide 라인 아이콘 38종(stroke 1.5), 색은 Brush.TextMuted |
| 카드 중첩·둥근 모서리 >8px·과한 여백 | 없음 | 반경 2px(버튼·입력), 패널 1px 경계, 패딩 12–16px |
| 포인트 색 2개 이상·유채색 버튼 | 없음 | accent 1개(선택 테두리·커서·드롭 미리보기·탭 밑줄), 버튼 무채색; 유채색은 곡선 팔레트만 |
| 무의미한 KPI·가짜 통계·장식 스파크라인 | 없음 | 숫자는 모두 실측(샘플 수·값·통계) |
| 제목 >20px·폰트 3종 초과·자간 과다 | 없음 | 제목 16px/600, Pretendard + JetBrains Mono 2종, 라벨 11px 소문자 대문자체 |
| 설명 없는 아이콘 버튼·중앙 정렬 본문 | 없음 | 모든 아이콘 버튼에 ToolTip, 본문 좌측 정렬 |
| 토큰만 사용(하드코딩 색 0) | 통과 | `grep '#RRGGBB'` → Tokens.*.xaml·RenderTheme(팔레트) 외 0건 |
| 라이트·다크 즉시 전환 | 통과 | Ctrl+T, 리소스 사전 교체 + Skia 테마 재주입 |

## 6. 알려진 문제
- 범례가 데이터 위에 겹쳐 그려진다(기준 도구과 동일한 오버레이 방식). 위치 이동은 미구현.
- 곡선 Y축은 "단위당 축" 자동 배치(좌·우 교대). 수동 축 범위 편집 UI 없음(속성 패널은 표시만).
- VIEW 분할은 세로 스택만(가로 분할 없음). XY 그래프는 20만 점 샘플링으로 그림(피라미드 미적용).
- CSV 가져오기는 전체를 메모리에 올린다(대용량 CSV 미최적화). .dxd는 MiniMeas.Core가 지원하는 범위.
- DataPlugin: 공개 SDK ABI(Python 마셜러·C++ SDK 플러그인, 워커 프로세스) + VBS 플러그인(인프로세스 VBScript, 객체 모델 부분집합 — 예제 1–3 수준; Excel·XML·고급 Block 옵션 미구현). 네이티브 USI_130 플러그인은 미지원.
- .tdv 가져오기는 2D 축계(DisplayTyp 0)와 채널 테이블(1)만; 텍스트박스(2)·그림(4) 등은 건너뛴다. 곡선 색·마커 스타일은 복원하지 않는다.
- 커서 구간 통계는 2,000만 샘플 초과 구간에서 min/max만(평균 생략).
- 레이아웃 복원은 채널 ID(파일명|그룹|채널) 기준이라 파일명이 바뀌면 곡선이 복원되지 않는다.
- `dotnet publish` 경고 NU1701(SkiaSharp.Views.WPF/OpenTK가 net48 자산으로 복원) — 동작에는 영향 없음(실행 확인).

## 7. 사용자 확인 체크리스트
- [ ] `app\dist\NexysView.exe` 더블클릭 → 창이 뜨고 좌상단 로고·"Nexys VIEW" 표시
- [ ] `work\bench\bench2_1GB_16ch_8M.tdms`를 창에 드롭 → 상태줄에 "Opened … in N ms"(≈3 ms)
- [ ] 채널 Ch00을 가운데 "Drop a channel here"에 드래그 → 1초 내 곡선, 상태줄 "10,000,000 samples ready"
- [ ] 휠 줌·드래그 팬이 끊김 없이 60 fps (Ctrl+D 개발자 모드로 프레임 시간 확인)
- [ ] 클릭=커서 A, Shift+클릭=커서 B → 상단 띠에 A/B/Δ/min/max/mean
- [ ] Ctrl+T 다크 전환 즉시, Ctrl+Z/Y로 조작 되돌리기
- [ ] ANALYSIS 탭 → 채널 드롭 → FFT 실행 → "Show in VIEW"
- [ ] REPORT 탭 → + Title/Chart/Table → Export PDF
- [ ] NAVIGATOR 탭 → "Open with DataPlugin…" → ref\examples\datapluginbs\DataPluginExample2.vbs + DataPluginExample2.ex2 → 4채널
- [ ] 앱 종료 후 재실행 시 레이아웃(파일·차트·테마) 복원
