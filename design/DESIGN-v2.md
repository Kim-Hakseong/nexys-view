# Nexys VIEW — 디자인 v2 "Instrument Gray" (계측기 소프트웨어 스타일)

v1(design/DESIGN.md)의 §4 조작·§5 성능·§6 금지 목록은 그대로 유효하다. 이 문서는 §1~3(스타일)을 교체한다.
근거: docs/design-v2-research.md(기준 도구 실행 관찰, DXD 도구 매뉴얼 캡처,  tool B·tool C 공식 이미지).

## 0. 원칙
1. 크롬은 무채색 회색 2–3단, 흰 카드 없음, 모서리 0, 그림자 0, 1px 진한 구분선. (v1 §0-2 "색은 데이터만" 유지)
2. 플롯은 크롬과 **별도 토큰**: 기본 어두운 플롯 + 고채도 트레이스. 밝은 플롯은 설정 옵션.
3. 값은 판독창(readout) 문법으로: 어두운 상자, 채널 색 띠, 큰 모노 숫자, 단위·라벨 11px 대문자.
4. 상태는 LED 점 + 텍스트로 명시. 포인트색은 1개, 선택·활성에만.
5. 밀도는 계측기 수준(행 24px, 본문 12px). 제목은 14px/600까지.

## 1. 토큰
### 1.1 라이트 "Instrument Gray" (기본)
| 토큰 | 값 | 용도 |
|---|---|---|
| bg | #CFD3D8 | 창 배경 |
| panel | #DDE0E4 | 패널, 툴바, 상태줄 |
| panel-alt | #E7E9EC | 패널 헤더, 표 헤더, 줄무늬, 호버 |
| border | #9EA4AC | 1px 구분선·격자선 |
| text | #141618 | 본문 |
| text-muted | #4F555C | 라벨, 보조 |
| text-faint | #868C94 | 비활성 |
| accent | #2E6DB4 | 선택·활성(모드 탭 바, 선택 행 바, 포커스 링, 커서) |
| grid | #C4C9CF | 크롬 안 보조선(표 내부 옅은 선) |
| led-idle / led-ready / led-busy / led-error | #8A9099 / #2FA84F / #E08A1E / #D2423C | 상태 LED |

### 1.2 다크
| 토큰 | 값 |
|---|---|
| bg | #0E0F11 |
| panel | #1E2126 |
| panel-alt | #262A30 |
| border | #3A3E45 |
| text | #F2F2F0 |
| text-muted | #9A9DA3 |
| text-faint | #5C5F66 |
| accent | #D9F23A |
| grid | #2C3036 |
| led-* | 라이트와 동일 |

### 1.3 플롯 (Theme/Tokens.Plot.xaml, 테마와 독립)
| 토큰 | 어두운 플롯(기본) | 밝은 플롯(옵션) |
|---|---|---|
| plot-bg | #121416 | #FFFFFF |
| plot-grid | #2A2E33 | #D7D7D7 |
| plot-axis-text | #B8BDC4 | #4F555C |
| plot-frame | #3A3E45 | #9EA4AC |
| plot-cursor | #FFFFFF (accent 아님: 트레이스와 구분) | #141618 |
| 트레이스 8색 | #FFD400 #00D1FF #4CFF4C #FF5CE1 #FF8A00 #8FA8FF #FF5A5A #B6FF00 | v1 팔레트 #1F77B4 #E8601C #2CA02C #9467BD #17BECF #D62728 #8C564B #BCBD22 |
9번째 트레이스부터 명도 변형(v1 규칙). 플롯 선택(밝음/어두움)은 LayoutStore에 저장(자동화 실행은 저장 안 함).

## 2. 타이포·밀도
- Pretendard 12px 본문, 11px 라벨(대문자, 자간 없음), 제목 최대 14px/600. 모노(JetBrains Mono)는 숫자·값·눈금만.
- 판독창 값 20–24px/500 모노, 단위 11px muted, 라벨 11px 대문자 muted.
- 행 24px(트리·표·목록), 패널 패딩 8px, 거터 6px, 창 가장자리 8px, 툴바 32px, 상태줄 24px.
- 모서리 0. 그림자 0. 1px 선으로만 구분.

## 3. 구성 요소
- **툴바**(32px): 아이콘 16px + 라벨 11px(옆), 기능 그룹 사이 1px 세로선(border), 배경 panel.
- **모드 탭**: 눌린 버튼 형태 — 활성 = panel-alt 배경 + 하단 2px accent, 비활성 = 투명, 라벨 11px 대문자 600.
- **패널**: 플랫(panel 배경) + 1px border, 헤더 24px panel-alt + 11px 대문자 라벨.
- **표**: 1px 격자(border), 줄무늬(panel/panel-alt), 숫자 우측 정렬·소수점 정렬(모노), 단위 열 분리, 헤더 panel-alt.
- **트리**: 행 24px, 아이콘 16px muted, 이름 12px, 메타 11px faint 우측, 선택 = panel-alt + 좌측 2px accent.
- **판독창**: plot-bg 상자, 좌측 채널 색 띠 3px, 라벨 11px 대문자 plot-axis-text, 값 모노 20–24px plot 텍스트(#F2F2F0), 단위 11px.
- **상태**: 상단 툴바 우측과 하단 상태줄에 LED 점 8px(idle 회색 · ready 초록 · busy 주황 · error 빨강) + 텍스트.
- **범례**: 플롯 우상단 대신 차트 헤더 아래 띠 또는 우측 패널 — 채널 색 띠 3px + 이름 + 단위, 클릭 숨김.
- **신뢰 단서**: 모든 값에 단위, 숫자 잘림 없음(넘치면 지수 표기), 파괴적 동작 확인(파일 닫기·차트 제거는 Undo 가능하므로 확인 없이), 로딩 중에도 마지막 유효 값 유지(판독창은 "—" 대신 이전 값 + busy LED).

## 4·5·6. 조작·성능·금지 목록
v1 §4, §5, §6을 그대로 적용. 추가 금지: 흰 카드 + 큰 반경, 유채색 채움 버튼(accent 바/테두리만 허용).

## 7. 완료 판정
- 라이트·다크·플롯(어두움/밝음) 토큰만 사용(하드코딩 색 0개, grep 확인), 캡처 라이트·다크 × NAVIGATOR/VIEW/ANALYSIS + VeriStand 1장, §6 자가 점검표, P2/P3·메모리 관문 유지.
