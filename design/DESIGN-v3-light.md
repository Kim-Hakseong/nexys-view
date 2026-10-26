# Nexys VIEW — 디자인 v3 라이트 "Warm Instrument" (계측기 구조 + ref 2·3 재질·타이포)

v2(design/DESIGN-v2.md)의 **구조**(3패널, 모드 탭, 패널 헤더, 판독창, LED, 플롯 토큰 분리, 트리 24px, §4 조작·§5 성능·§6 금지)는 유지한다.
v3는 라이트 테마의 **재질과 타이포**를 design/2_numbers.jpg(큰 세리프 숫자, 델타 줄, 알약 탭, 둥근 카드, 점 진행), design/3_chart_detail.jpg(세리프 값 + 단위, "In range" 알약, 점 범위 막대, 카드 헤더, 점 격자 차트)에서 가져온다.
다크 테마는 색을 바꾸지 않고 구조(반경·알약·세리프 판독·점 격자 옵션)만 공유한다.

## 0. 원칙
1. 웜 그레이 종이 위의 흰 카드: 배경 #ECEAE5, 카드 #F7F6F3, 1px #D9D6CF 구분. 그림자 0. 모서리는 카드 10 / 내부 카드 8 / 컨트롤 6 / 알약 999.
2. 포인트는 2개: **검정 채움**(선택·주 동작: 활성 탭, 주 버튼, 선택 행) + **주황 #E8601C**(데이터 강조: 커서, 스파크라인, 포커스 링, 범위 막대의 현재 점). 색은 여전히 데이터에만.
3. 큰 숫자는 세리프(Instrument Serif, OFL) 28/36px, 라벨 11px 대문자, 단위 11–12px muted. 작은 숫자·눈금·표는 모노 유지.
4. 선택·토글은 알약(pill): 모드 탭, 버튼, 칩, 콤보, 목록 선택 행.
5. 밀도는 v2 그대로(행 24px, 본문 12px, 툴바 36px, 차트 헤더 36px). 여백 리듬 8/10.

## 1. 토큰 (app/NexysView.App/Theme/Tokens.Light.xaml)
| 토큰 | 값 | 용도 |
|---|---|---|
| bg | #ECEAE5 | 창 배경(웜 그레이) |
| panel | #F7F6F3 | 패널·카드·툴바·상태줄 |
| panel-alt | #EFEDE8 | 패널 헤더, 내부 카드, 호버, 줄무늬 |
| border | #D9D6CF | 1px 구분선, 격자 |
| border-strong | #B9B5AC | 칩·입력·표 외곽 |
| text | #17181A | 본문 |
| text-muted | #6B6A66 | 라벨, 보조 |
| text-faint | #A6A39C | 비활성, 메타 |
| accent | #17181A | 선택·주 동작 채움(탭·주 버튼·선택 행) — 글자는 bg |
| accent2 | #E8601C | 커서, 스파크라인, 포커스 링, 범위 막대 현재 점 |
| grid | #E3E0D9 | 크롬 안 보조선 |
| led-* | #A6A39C / #2FA84F / #E08A1E / #D2423C | idle / ready / busy / error |

다크(Tokens.Dark.xaml)는 v2 값 그대로 + border-strong #4A4F57, accent2 #E8601C 추가.

### 1.1 플롯 (Tokens.PlotLight.xaml / Tokens.PlotDark.xaml)
| 토큰 | 밝은 플롯(v3 기본값은 테마 따름) | 어두운 플롯 |
|---|---|---|
| plot-bg | #FBFAF8 | #121416 |
| plot-grid | #D9D6CF, **점 격자**(`Plot.GridStyle`="dots") | #2A2E33 선 격자 |
| plot-axis-text | #6B6A66 | #B8BDC4 |
| plot-frame | #D9D6CF | #3A3E45 |
| plot-cursor | #E8601C | #FFFFFF |
| 트레이스 | v1 8색(#1F77B4 #E8601C #2CA02C #9467BD #17BECF #D62728 #8C564B #BCBD22) | v2 고채도 8색 |
플롯 영역 모서리 6px(PlotRenderer.PlotRadius), 코너는 패널색으로 되칠한 뒤 둥근 프레임을 그린다.

## 2. 타이포
| 역할 | 글꼴 | 크기/굵기 |
|---|---|---|
| 본문·트리·표 | Pretendard | 12 / 400 |
| 라벨(대문자) | Pretendard | 11 / 600 |
| 섹션 제목 | Pretendard | 12–14 / 600 |
| 판독 값(패널·분석 카드) | **Instrument Serif** | 36 / 400 (Readout.ValueLarge), 28 (Text.ValueLarge) |
| 판독 값(차트 띠) | JetBrains Mono | 14 / 500 |
| 숫자·눈금·표 값 | JetBrains Mono | 11–12 |
| 단위·상태 줄 | Pretendard | 11–12 muted (값 위 "last sample · 42% of range", ref 2의 "-1 pts" 자리) |

## 3. 구성 요소
- **툴바** 36px: 로고 + 이름, 알약 그룹(PillGroup: panel-alt 배경·1px border·반경 999) 안의 모드 탭(활성 = accent 채움 + bg 글자, 120 ms 불투명도 애니메이션), 아이콘+라벨 버튼, 우측 LED.
- **패널 카드** Panel.Card: panel 배경, 1px border, 반경 10, 클립. 헤더 Panel.Header 28px 투명(아이콘 16 + 11px 대문자 + 우측 메타 faint).
- **내부 카드** Card.Inner: panel-alt, 반경 8 (데이터 소스 행 카드, 분석 결과 카드).
- **판독창** Readout.Box: plot-bg 상자, 반경 8, 3px 채널색 띠, 라벨 11px 대문자, 상태 줄, 세리프 값 36 + 단위. 네 개를 나란히 두면 ref 2의 숫자 카드.
- **점 범위 막대** PropertiesPanel.RangeBar: 36개 점(border색), 현재값 주변 ±8점을 muted로 밝히고 현재 점은 accent2, 위에 현재값 알약(panel 배경·border-strong). 양 끝에 min/max 모노 11px (ref 3 "13.5 ···●··· 18").
- **칩** Chip: 알약, 1px border-strong, 18px, 11px 대문자 (트리 파일 행 "TDMS · 21 ch", 데이터 소스 "TIME: SYSTEM TIME").
- **버튼**: 기본 = 알약 외곽선(border-strong), Button.Primary = accent 채움 + bg 글자(RUN, DROP ALL (n), COMPARE…, USE AS X), Button.Icon = 24px 투명, Button.Tool = 툴바 아이콘+라벨.
- **목록/콤보/체크**: ListBoxItem 선택 = accent 알약 + bg 글자; ComboBox 알약; CheckBox 20px 둥근 사각 accent 채움.
- **트리** 24px 행, 선택 = panel-alt + 2px accent2 왼쪽 띠; 파일 행 칩, 채널 행 메타(단위 · 샘플 수 compact), 선택 채널 행에 **점 스파크라인**(피라미드 최상위 16점, accent2 채움, 48×14).
- **차트 카드** PlotFrame: 헤더 36px(그립 · 차트 아이콘 · 제목 · 메타 "n curves · X <채널> · rel/clock · align <mode>" · "USE <TIME> AS X" 알약 · 닫기), 판독 띠(커서 A/B/Δx 상자 + 곡선당 1상자), 플롯(둥근 6px, 점 격자), 하단 디지털 레인 16px(라벨은 plot-bg 백킹 위).
- **데이터 소스**(NAVIGATOR 모드): 파일당 행 카드(아이콘 · 이름/경로 · 칩 FORMAT/CH/TIME · 숫자 4열 우측 모노 · 닫기).
- **ANALYSIS 통계**: min/max/mean/RMS 세리프 카드 4개 + count/std/sum 작은 표.
- **REPORT**: 종이 카드 반경 10 클립, 종이는 뷰포트 폭에 맞춰 축소(A4 794px 기준 비율 유지).

## 4. 구현 규칙
- 색은 토큰만(Theme/Tokens.*.xaml) — 코드에 hex 없음. 반경은 Radius.* 리소스만.
- 알약 채움 위 글자: 암시적 TextBlock 스타일이 Foreground를 덮어쓰므로 ContentPresenter 안에 컨트롤 Foreground에 바인딩한 TextBlock 스타일을 둔다(Button.Primary, ModeTab, ListBoxItem).
- 애니메이션은 120 ms 불투명도 1개(모드 탭). 그 외 없음.
- 자동화(--shot/--selftest/--v3/--stress*)는 layout.json·사용자 설정을 쓰지 않는다.
