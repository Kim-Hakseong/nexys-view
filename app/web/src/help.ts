// Help guide (F1 / HELP button): sectioned user guide modal + spotlight feature tour (auto-started once on the first start).
import { bridge } from './bridge';
import { I } from './icons';
import { State } from './types';
import { esc, h } from './util';

type Place = 'top' | 'bottom' | 'left' | 'right' | 'auto';
interface Step { sel: string | null; mode?: string; title: string; body: string; place?: Place; pad?: number; }

const kbd = (k: string) => `<kbd>${esc(k)}</kbd>`;
const sub = (t: string) => `<p class="sub">${t}</p>`;

export const TOUR_STEPS: Step[] = [
  { sel: null, title: 'Nexys VIEW에 오신 것을 환영합니다 👋',
    body: `<p>Nexys VIEW는 <b>TDMS · TDM · CSV 측정 로그</b>를 열어 곡선으로 보고, 계산하고, 보고서로 만드는 뷰어입니다.</p>${sub(`약 1분이면 핵심 기능을 둘러볼 수 있어요. 언제든 ${kbd('ESC')} 키로 닫을 수 있습니다.`)}` },
  { sel: '.tools-modes', place: 'bottom', pad: 6, title: '1. 두 가지 화면',
    body: `<p>툴바 첫 그룹의 <b>NAVIGATOR</b>(파일·채널) 와 <b>VIEW</b>(차트) 두 화면입니다. 활성 화면은 배경과 밑줄로 표시돼요.</p>${sub(`${kbd('Ctrl+1')} / ${kbd('Ctrl+2')}로도 바꿀 수 있어요.`)}` },
  { sel: '[data-a="open"]', place: 'bottom', title: '2. 파일 열기',
    body: `<p><b>OPEN</b>을 누르거나 파일을 <b>창 어디에나 끌어다 놓으면</b> 열립니다. 처음에는 내장 샘플(Sample.tdms)이 열려 있어요.</p>${sub(`.tdms · .tdm/.tdx · .csv/.txt · .tdv, DataPlugin은 ${kbd('Ctrl+Shift+O')}.`)}` },
  { sel: '.nav', mode: 'VIEW', place: 'right', pad: 4, title: '3. 채널 트리',
    body: `<p>파일 ▸ 그룹 ▸ 채널이 트리로 보입니다. 채널을 <b>끌어서 차트로 옮기는</b> 것이 기본 동작이고, <b>파일이나 그룹을 통째로 끌어다 놓으면</b> 바로 차트가 됩니다.</p>${sub(`검색은 부분 문자열이나 와일드카드(<code>Sine*</code>), <b>DROP ALL</b>은 일치 채널로 차트를 한 번에 만듭니다. TREE/FLAT으로 계층을 접고 펼쳐요.`)}` },
  { sel: '.view', mode: 'VIEW', place: 'left', pad: 4, title: '4. 차트',
    body: `<p>채널을 <b>빈 영역</b>에 놓으면 새 차트, <b>차트 위</b>에 놓으면 곡선 추가, <b>왼쪽/오른쪽 축 영역</b>에 놓으면 축 지정, <b>아래 x축 영역</b>에 시간 채널을 놓으면 X축이 됩니다.</p>${sub(`휠 = 줌, 드래그 = 이동, ${kbd('Shift')}+드래그 = 영역 줌, 더블클릭 = 전체 보기. 클릭 = 커서 A, ${kbd('Shift')}+클릭 = 커서 B.`)}` },
  { sel: '.props', mode: 'VIEW', place: 'left', pad: 4, title: '5. 속성 패널',
    body: `<p>고른 채널·곡선의 정보와 <b>커서 A/B 값</b>이 여기에 보입니다.</p>${sub(`곡선 버튼: STEP/LINE, RIGHT AXIS, SET AS X, REMOVE. 차트 버튼: DIGITAL LANES, GRID, ZOOM FULL. 시간축이면 REL/ABS/NUMERIC 눈금.`)}` },
  { sel: '.view', mode: 'VIEW', place: 'left', pad: 4, title: '6. 밴드 커서와 상세 차트',
    body: `<p>${kbd('Ctrl+B')} 또는 헤더의 <b>A / B</b> 알약을 누르면 <b>밴드 커서</b>가 됩니다. 손잡이를 끌어 폭을, 안쪽을 끌어 위치를 바꾸고 판독 띠에서 구간의 min / max / mean / RMS / p-p를 읽어요.</p>${sub(`차트 메뉴(⋯) → <b>상세 차트 연결</b>: 아래에 상세 차트가 생기고 밴드 구간을 확대해 보여줍니다. 밴드를 움직이면 상세가, 상세를 줌·팬하면 밴드가 따라와요.`)}` },
  { sel: '.pages', mode: 'VIEW', place: 'bottom', pad: 4, title: '7. 페이지와 창',
    body: `<p>VIEW 위의 <b>페이지 탭</b>으로 차트를 묶습니다(+ 로 추가, 더블클릭 이름, 드래그 순서, × 제거). GRID 1 COL / 2 COL, AUTO PAGE는 차트가 n개를 넘으면 새 페이지를 만들어요.</p>${sub(`차트 메뉴(⋯) → <b>새 창으로</b>(또는 그립을 창 밖으로 드래그)하면 별도 창에 차트가 뜨고, 밴드·X링크·커서가 창을 넘어 함께 움직입니다. 창을 닫으면 차트는 주 창으로 돌아와요.`)}` },
  { sel: '.view', mode: 'VIEW', place: 'left', pad: 4, title: '8. 줌과 내보내기',
    body: `<p><b>오른쪽 드래그</b> = 영역 확대(${kbd('Shift')} = X만, ${kbd('Ctrl')} = Y만), 휠 = 커서 기준 줌, 더블클릭 = 전체, ${kbd('Backspace')} = 줌 되돌리기(20단계).</p>${sub(`차트 메뉴(⋯)에서 밴드 구간을 <b>CSV / TDMS</b>로 저장하고, 차트 PNG를 복사·저장하고, <b>+ TABLE</b>로 값 표를 엽니다. 같은 X를 쓰는 차트는 팬·줌이 함께 움직여요(LINK).`)}` },
  { sel: '[data-a="help"]', place: 'bottom', title: '9. 도움말은 언제든',
    body: `<p><b>?</b> 버튼이나 ${kbd('F1')}을 누르면 사용 가이드와 단축키 목록, <b>"기능 투어 다시 보기"</b>가 있습니다.</p>${sub(`이제 파일을 열고 채널이나 그룹을 끌어다 놓아 보세요 🚀`)}` },
];

// ---------- spotlight tour
class Tour {
  root: HTMLElement; spot: HTMLElement; card: HTMLElement; i = 0; startMode: string; done = false;
  private onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); this.finish(); } else if (e.key === 'ArrowRight') this.go(this.i + 1); else if (e.key === 'ArrowLeft') this.go(this.i - 1); };
  private onResize = () => this.place();
  constructor(private steps: Step[], private setMode: (m: string) => void, private state: () => State | null, private onEnd: () => void) {
    this.startMode = state()?.mode ?? 'VIEW';
    this.root = h(`<div class="tour" role="dialog" aria-modal="true" aria-label="기능 투어"><div class="tour-bg"></div><div class="tour-spot"></div><div class="tour-card"></div></div>`);
    this.spot = this.root.querySelector('.tour-spot')!; this.card = this.root.querySelector('.tour-card')!;
    this.root.querySelector('.tour-bg')!.addEventListener('click', () => this.finish());
    this.card.addEventListener('click', e => {
      const t = (e.target as HTMLElement).closest('[data-t]') as HTMLElement | null; if (!t) return;
      const a = t.dataset.t; if (a === 'next') this.go(this.i + 1); else if (a === 'prev') this.go(this.i - 1); else this.finish();
    });
    window.addEventListener('keydown', this.onKey, true); window.addEventListener('resize', this.onResize);
    document.body.append(this.root);
  }
  async go(i: number) {
    if (i < 0) return; if (i >= this.steps.length) { this.finish(); return; }
    this.i = i; const st = this.steps[i];
    if (st.mode && this.state()?.mode !== st.mode) this.setMode(st.mode);
    // wait for the target (mode switch arrives through the host state round-trip)
    let el: HTMLElement | null = null;
    for (let k = 0; k < 40; k++) { el = st.sel ? document.querySelector(st.sel) : null; if (!st.sel || el) break; await new Promise(r => setTimeout(r, 25)); }
    const n = this.steps.length, last = i === n - 1;
    this.card.innerHTML = `<div class="tour-top"><span class="tour-step">STEP ${i + 1} / ${n}</span><button class="xbtn" data-t="close" aria-label="닫기">${I.close()}</button></div>
      <div class="tour-title">${st.title}</div><div class="tour-body">${st.body}</div>
      <div class="tour-bar"><i style="width:${Math.round((i + 1) / n * 100)}%"></i></div>
      <div class="tour-foot"><button class="tour-skip" data-t="skip">건너뛰기</button><span class="sp"></span>${i > 0 ? `<button class="pill h30" data-t="prev">이전</button>` : ''}<button class="pill h30 primary" data-t="next">${last ? `시작하기 ${I.check()}` : '다음 →'}</button></div>`;
    this.place();
  }
  place() {
    const st = this.steps[this.i]; const el = st.sel ? document.querySelector(st.sel) as HTMLElement | null : null;
    const vw = window.innerWidth, vh = window.innerHeight, m = 16;
    const cw = this.card.offsetWidth || 380, ch = this.card.offsetHeight || 200;
    if (!el) {
      this.root.classList.add('nospot'); Object.assign(this.spot.style, { top: '-9999px', left: '-9999px', width: '0', height: '0' });
      Object.assign(this.card.style, { top: Math.max(m, (vh - ch) / 2) + 'px', left: Math.max(m, (vw - cw) / 2) + 'px' }); return;
    }
    this.root.classList.remove('nospot');
    const r = el.getBoundingClientRect(), p = st.pad ?? 8;
    const s = { top: Math.max(0, r.top - p), left: Math.max(0, r.left - p), w: r.width + 2 * p, h: r.height + 2 * p };
    Object.assign(this.spot.style, { top: s.top + 'px', left: s.left + 'px', width: s.w + 'px', height: s.h + 'px' });
    const below = s.top + s.h + m + ch <= vh - m, above = s.top - m - ch >= m, rightOk = s.left + s.w + m + cw <= vw - m, leftOk = s.left - m - cw >= m;
    const pl = st.place ?? 'auto'; let top: number, left: number;
    const clampY = (y: number) => Math.min(Math.max(m, y), vh - ch - m), clampX = (x: number) => Math.min(Math.max(m, x), vw - cw - m);
    const side = (pl === 'right' || pl === 'left') && (rightOk || leftOk);
    if (side) { top = clampY(s.top + s.h / 2 - ch / 2); left = (pl === 'right' ? rightOk : !leftOk) ? s.left + s.w + m : s.left - m - cw; }
    else if (below || above) { const bottom = pl === 'top' ? !above : below; top = bottom ? s.top + s.h + m : s.top - m - ch; left = clampX(s.left + s.w / 2 - cw / 2); }
    else { top = clampY(s.top + s.h / 2 - ch / 2); left = clampX(s.left + s.w / 2 - cw / 2); }   // large target: card over its centre
    Object.assign(this.card.style, { top: top + 'px', left: left + 'px' });
  }
  finish() {
    if (this.done) return; this.done = true;
    window.removeEventListener('keydown', this.onKey, true); window.removeEventListener('resize', this.onResize);
    this.root.remove();
    if (this.state()?.mode !== this.startMode) this.setMode(this.startMode);
    this.onEnd();
  }
}

// ---------- help guide modal
const SECTIONS: { id: string; label: string; html: (app: any) => string }[] = [
  { id: 'start', label: '시작하기', html: app => `
    <button class="help-tour" data-h="tour">${I.play()} 기능 투어 다시 보기</button>
    <h3>Nexys VIEW에 오신 것을 환영합니다!</h3>
    <p>Nexys VIEW는 측정 로그를 빠르게 열어 보는 뷰어예요. TDMS(2.0)·TDM/TDX·CSV/TXT 파일과 DataPlugin(VBS/Python/SDK)으로 읽는 형식을 열 수 있고, 1,000만 샘플 곡선 10개도 끊김 없이 팬·줌합니다.</p>
    <h4>처음 사용하신다면</h4>
    <ol><li>처음에는 내장 샘플 <code>Sample.tdms</code>가 열려 있어요. 내 파일은 <b>OPEN</b>이나 창에 끌어다 놓기로 엽니다.</li><li>왼쪽 NAVIGATOR 트리에서 채널을 끌어 가운데 빈 영역에 놓으면 차트가 됩니다.</li><li>휠로 줌, 드래그로 이동, 클릭으로 커서 A, <kbd>Shift</kbd>+클릭으로 커서 B.</li><li>ANALYSIS로 계산하고 REPORT로 보고서를 만들어 PNG/PDF로 저장하면 끝!</li></ol>
    <h4>샘플 파일</h4>
    <p>합성 신호 8채널(Time·Sine 1 Hz·Sine 5 Hz·Ramp·Temperature·Square·Counter·State, 20 s @ 1 kHz)이 <code>%LOCALAPPDATA%\\NexysView\\sample\\</code>에 한 번 만들어집니다. 데이터 소스 카드의 ×로 닫을 수 있어요.</p>
    <div class="help-note">문제나 제안은 <a href="#" data-url="${esc(app.issues || '')}">GitHub Issues</a>에 Windows 버전·재현 순서·파일 종류(채널·샘플 수)와 함께 남겨 주세요. 저장소: <a href="#" data-url="${esc(app.repo || '')}">${esc(app.repo || '')}</a></div>` },
  { id: 'screens', label: '화면 구성', html: () => `
    <h3>두 가지 화면</h3><p>툴바 첫 그룹의 NAVIGATOR / VIEW 버튼 또는 <kbd>Ctrl+1</kbd> / <kbd>Ctrl+2</kbd>로 바꿉니다. 왼쪽 NAVIGATOR 트리는 VIEW에서도 그대로 있어요. (ANALYSIS·REPORT는 설정 파일의 ShowLegacyTabs로만 표시)</p>
    <div class="help-card"><b>페이지</b><p>VIEW 위의 탭으로 차트를 페이지에 나눠 둡니다. + 추가, 더블클릭 이름 바꾸기, 드래그 순서 바꾸기, × 제거(차트는 다른 페이지로 이동). GRID 1 COL / 2 COL, AUTO PAGE ON이면 차트가 n개를 넘을 때 새 페이지가 생깁니다. <kbd>Ctrl+PageDown</kbd> / <kbd>Ctrl+PageUp</kbd>으로 이동.</p></div>
    <div class="help-card"><b>여러 창</b><p>차트 메뉴(⋯) → 새 창으로, 또는 헤더 그립을 창 밖으로 드래그하면 차트가 별도 창으로 나갑니다. 같은 세션이라 X축 링크·밴드·개요/상세·커서가 창을 넘어 함께 움직이고, 창을 닫거나 RETURN ALL을 누르면 차트는 주 창으로 돌아옵니다. 창 위치·크기는 기억됩니다.</p></div>
    <div class="help-card"><b>NAVIGATOR</b><p>파일 ▸ 그룹 ▸ 채널 트리와 데이터 소스 카드(채널·샘플 수·크기·열기 시간). 이름에 <code>.</code>이 있는 채널은 계층으로 접히고(TREE/FLAT), 검색은 부분 문자열이나 <code>*</code> <code>?</code> 와일드카드. <b>DROP ALL (n)</b>은 일치 채널로 차트를 만듭니다. 파일을 <kbd>Ctrl</kbd>-클릭으로 여러 개 고르고 <b>COMPARE n FILES</b>로 공통 채널을 파일별 곡선으로 겹쳐 봐요(정렬: 시작 시각 / 0 s / 수동).</p></div>
    <div class="help-card"><b>VIEW</b><p>차트 영역. 채널을 놓는 위치에 따라 새 차트 / 곡선 추가 / 축 지정 / X축 지정이 되고, <b>파일·그룹 노드를 놓으면 바로 차트</b>가 됩니다(드롭 존의 BY UNIT = 단위별 Y축으로 1개, PER CHANNEL = 채널마다). 차트마다 커서 A/B 표 또는 밴드 판독 띠, 범례, 메뉴(⋯)가 있어요.</p></div>
    <div class="help-card"><b>PROPERTIES</b><p>곡선: LINE / STEP / POINTS, 굵기 1·1.5·2, 색, 축 L/R · SHARED/OWN, 숨김. Y축: AUTO/MANUAL 범위, LOG, 좌·우. 설정은 레이아웃에 저장됩니다.</p></div>
    <div class="help-card"><b>ANALYSIS</b><p>왼쪽 함수 목록 → 가운데 입력 채널·미리보기·결과 → 오른쪽 파라미터. 결과 채널은 <code>Analysis/Results</code> 그룹에 쌓입니다.</p></div>
    <div class="help-card"><b>REPORT</b><p>왼쪽 블록 목록, 가운데 A4 미리보기, 오른쪽 속성. PNG(2×)·PDF로 내보냅니다.</p></div>` },
  { id: 'chart', label: '차트 조작', html: () => `
    <h3>차트 조작</h3><p>채널을 끌어다 놓는 위치가 곧 명령입니다.</p>
    <table class="help-table"><tr><td>새 차트</td><td>빈 영역(점선 드롭 존)에 드롭</td></tr><tr><td>곡선 추가</td><td>차트 플롯 위에 드롭</td></tr><tr><td>왼쪽/오른쪽 축 지정</td><td>차트의 좌·우 축 영역에 드롭</td></tr><tr><td>X축 지정</td><td>아래 x축 영역에 시간 채널 드롭 (또는 속성 패널 SET AS X / USE … AS X)</td></tr><tr><td>XY 차트</td><td>트리에서 <kbd>Ctrl</kbd>-클릭으로 채널 2개 고른 뒤 드롭</td></tr><tr><td>값 테이블</td><td><kbd>Alt</kbd>+드롭</td></tr></table>
    <h4>보기</h4>
    <table class="help-table"><tr><td>줌 / 팬</td><td>휠(커서 기준) / 드래그</td></tr><tr><td>영역 확대</td><td>오른쪽 드래그 (점선 사각) · <kbd>Shift</kbd> = X만 · <kbd>Ctrl</kbd> = Y만</td></tr><tr><td>전체 보기 / 되돌리기</td><td>더블클릭 (ZOOM FULL) / <kbd>Backspace</kbd> (히스토리 20단계)</td></tr><tr><td>커서 A / B / 해제</td><td>클릭 / <kbd>Shift</kbd>+클릭 / 우클릭</td></tr><tr><td>범례</td><td>클릭 = 숨김, 드래그 = 다른 차트로 이동. 헤더 그립 드래그 = 차트 순서 교환</td></tr></table>
    <h4>시간축과 계단 신호</h4>
    <p>X축이 시간 채널이면 눈금이 <code>mm:ss.fff</code>(상대) 또는 <code>HH:mm:ss</code>(절대)로 바뀌고 커서 값은 원본 샘플을 읽습니다. 정수·불리언·값이 8개 이하인 채널은 자동으로 계단으로 그려지고, <b>DIGITAL LANES</b>를 켜면 플롯 아래 0/1 레인으로 쌓여요.</p>` },
  { id: 'band', label: '밴드·상세 보기', html: () => `
    <h3>밴드 커서와 개요/상세 연동</h3>
    <p>${kbd('Ctrl+B')} 또는 차트 헤더의 <b>A / B</b> 알약을 누르면 밴드 커서로 바뀝니다. 밴드는 시작·끝 두 손잡이와 그 사이의 반투명 영역입니다.</p>
    <table class="help-table"><tr><td>폭 조정</td><td>손잡이(가장자리) 드래그</td></tr><tr><td>통째 이동</td><td>영역 안쪽 드래그</td></tr><tr><td>1 px / 10 px 이동</td><td><kbd>←</kbd> <kbd>→</kbd> / <kbd>Shift</kbd>+<kbd>←</kbd> <kbd>→</kbd></td></tr><tr><td>정확한 값</td><td>PROPERTIES → BAND 시작·끝 입력 후 <kbd>Enter</kbd></td></tr></table>
    <p>판독 띠에는 밴드 구간의 곡선별 <b>min / max / mean / RMS / p-p</b>와 구간 길이가 보입니다. 샘플이 아주 많으면 mean·RMS 앞에 ≈(표본 추정)가 붙고, CSV 내보내기는 항상 정확한 값입니다.</p>
    <h4>상세 차트</h4>
    <p>차트 메뉴(⋯) → <b>상세 차트 연결</b>(또는 PROPERTIES의 LINK DETAIL). 개요 차트 아래에 상세 차트가 생기고 밴드 구간만 확대해서 보여줍니다. 밴드를 움직이면 상세의 X범위가 바로 따라오고, 상세에서 줌·팬하면 개요의 밴드가 따라갑니다. 상세에 보일 채널은 개요 범례 클릭, PROPERTIES의 SHOW IN DETAIL, 또는 상세 차트에 채널 드롭으로 바꿉니다.</p>
    <h4>X축 링크</h4>
    <p>같은 X채널(또는 같은 파일·단위)을 쓰는 차트는 기본으로 팬·줌이 함께 움직입니다. 헤더의 <b>LINK</b> 알약으로 차트별로 끌 수 있고, 링크된 차트에는 개요의 밴드가 연한 점선으로 표시됩니다.</p>` },
  { id: 'bus', label: 'BUS (1553 · 429)', html: () => `
    <h3>BUS — MIL-STD-1553 / ARINC 429 캡처</h3>
    <p><code>.mon</code> 캡처를 열면 툴바에 <b>BUS</b>가 생겨요(<kbd>Ctrl+3</kbd>). 파일이 없으면 빈 화면에서 내장 샘플(1553 20,000건 · 429 2,000건)을 바로 열 수 있습니다.</p>
    <h4>메시지 리스트</h4><p>100만 건도 그대로 스크롤되고, 열 머리글을 누르면 정렬, <b>COLUMNS</b>로 열을 고릅니다. TIME은 상대 초 / 절대 µs / 이전 행과의 Δ로 바꿀 수 있고(설정에도 있음), 오류가 있는 행은 배경 톤으로 표시됩니다.</p>
    <h4>필터</h4><p>RT·SA 숫자, T/R, 버스 A/B, ERRORS ONLY, TEXT(데이터 hex·ICD 이름·"RT3" 같은 텍스트)를 조합하고 PRESET → SAVE로 조합을 저장합니다. 429는 LABEL(옥탈)·SDI·SSM·패리티 오류.</p>
    <h4>디코드·추이</h4><p>행을 고르면 명령어·상태어·오류·활동 비트와 데이터 워드(hex·bit·10진)가 보여요. ICD가 있으면 워드 이름과 EU 값이 함께 나오고(ICD 버튼으로 적용/해제), 워드의 ▸ trend 또는 <kbd>Enter</kbd>로 추이 차트를 그립니다. STATS는 메시지 수·RT별 분포·오류·버스 로드(근사), CSV / CSV·EU는 현재 목록과 디코드 결과를 내보냅니다.</p>
    <table class="help-keys">${[['행 이동', '↑ ↓ · PgUp PgDn · Home End'], ['첫 ICD 워드 추이', 'Enter'], ['선택 행 복사', 'Ctrl + C'], ['BUS 모드', 'Ctrl + 3']].map(r => `<tr><td>${r[0]}</td><td><kbd>${r[1]}</kbd></td></tr>`).join('')}</table>` },
  { id: 'icd', label: 'ICD 모듈', html: () => `
    <h3>ICD 모듈</h3><p>설정에서 <b>ICD export module</b>을 켜면 툴바에 ICD / EXPORT 버튼이 생깁니다. ICD 허브는 메시지(RT·SA·T/R·WC·주기·버스)와 워드(워드 번호·비트 시작·길이·타입·scale·offset·단위)의 작은 JSON(스키마 1.0)입니다.</p>
    <h4>IMPORT</h4><p>CSV·XLSX(첫 시트)는 워드당 1행. 열 이름을 자동으로 알아보고(message·rt·sa·tr·wc·period_ms·bus·word·bit_start·bit_len·type·scale·offset·unit·name), 못 알아본 열은 직접 고릅니다. 허브 JSON과 메시지 XML은 바로 열립니다.</p>
    <h4>EDIT</h4><p>메시지·워드 표에서 바로 고치고 추가·삭제합니다. RT·SA 0~31, WC 1~32, 비트 범위 겹침은 아래 메시지로 알려 줍니다(error는 내보내기를 막고 warning은 막지 않음).</p>
    <h4>EXPORT</h4><p>시뮬레이터 프로젝트(설정에서 템플릿 프로젝트 폴더 지정) · 메시지 XML · 커스텀 디바이스 XML · 자동화 스크립트(시뮬레이터 COM 감지 시). 미리보기에서 생성될 메시지·필드를 확인하고, XML은 내보낼 때 스키마 검사와 재판독 비교를 거칩니다.</p>` },
  { id: 'analysis', label: '분석', html: () => `
    <h3>ANALYSIS — 계산</h3><p>함수를 고르고 입력 채널을 정한 뒤 <b>RUN</b>. 결과 채널은 <b>SHOW IN VIEW</b>로 바로 차트가 됩니다.</p>
    <div class="help-card"><b>Statistics</b><p>min / max / mean / RMS / 표준편차 등 요약 통계.</p></div>
    <div class="help-card"><b>FFT</b><p>창 함수 Hann / Hamming / Blackman / Rect, 진폭 스펙트럼.</p></div>
    <div class="help-card"><b>IIR filter · FIR filter</b><p>biquad 저역/고역 통과, FIR 창 필터. 미리보기에서 응답을 확인해요.</p></div>
    <div class="help-card"><b>Derivative · Integral · Resample</b><p>미분, 적분, 새 샘플링 간격으로 재표본.</p></div>
    <div class="help-card"><b>Formula</b><p>수식 채널: <code>Ch("Results/y") = 2 * Ch("$1") + 1</code> 꼴. <code>$1</code>, <code>$2</code>는 입력 채널 순서예요.</p></div>` },
  { id: 'report', label: '보고서', html: () => `
    <h3>REPORT — 보고서</h3>
    <ol><li><b>+ TITLE / + TEXT / + CHART / + TABLE</b>로 블록을 쌓습니다. TITLE·TEXT는 행에서 바로 글을 고쳐요.</li><li>CHART는 현재 활성 차트를 커서·범례·축까지 스냅샷으로, TABLE은 그 차트 곡선의 채널·단위·샘플 수·min/max/mean/RMS 표로 넣습니다.</li><li>행의 <code>^</code>로 순서를 올리고 <code>×</code>로 지웁니다. 가운데 PAGE는 A4(794×1123, 96 dpi) 미리보기예요.</li><li><b>EXPORT PNG</b>(2배 해상도) 또는 <b>EXPORT PDF</b>(A4 1쪽)로 저장합니다.</li></ol>` },
  { id: 'export', label: '테이블·내보내기', html: () => `
    <h3>값 테이블</h3>
    <p>드롭 존의 <b>+ TABLE</b>(활성 차트의 채널) 또는 <kbd>Alt</kbd>+드롭으로 값 표를 엽니다. 1,000만 샘플도 가상 스크롤로 바로 보이고, 활성 차트의 커서 A 행이 강조되며 FOLLOW가 켜져 있으면 따라 스크롤합니다.</p>
    <p>셀을 끌어 영역을 고르고 <kbd>Ctrl+C</kbd>로 탭 구분 텍스트를 복사합니다(최대 100,000행). COPY ALL은 보이는 열 전체.</p>
    <h3>내보내기</h3>
    <table class="help-table"><tr><td>CSV / TDMS</td><td>차트 메뉴(⋯) 또는 PROPERTIES → EXPORT. 밴드가 있으면 밴드 구간만, 없으면 전체. 차트의 채널 전부.</td></tr><tr><td>차트 PNG</td><td>PNG COPY(클립보드) / PNG…(파일)</td></tr><tr><td>모든 채널 TDMS</td><td>헤더 SAVE (<kbd>Ctrl+S</kbd>)</td></tr></table>` },
  { id: 'keys', label: '단축키', html: () => `
    <h3>키보드 단축키</h3><p>마우스 없이도 빠르게 쓸 수 있어요.</p>
    <table class="help-keys">${[['열기', 'Ctrl + O'], ['DataPlugin으로 열기', 'Ctrl + Shift + O'], ['모든 채널 TDMS로 저장', 'Ctrl + S'], ['NAVIGATOR / VIEW / BUS', 'Ctrl + 1 · 2 · 3'], ['페이지 이동', 'Ctrl + PageDown / PageUp'], ['밴드 커서 ↔ 자유 커서', 'Ctrl + B'], ['줌 되돌리기 (20단계)', 'Backspace'], ['밴드 1 px / 10 px 이동', '← → / Shift + ← →'], ['테이블 선택 복사', 'Ctrl + C'], ['Undo / Redo', 'Ctrl + Z / Ctrl + Y'], ['테마 / 플롯 밝기 / 개발자 정보', 'Ctrl + T / Ctrl + P / Ctrl + Shift + D'], ['도움말 / 투어 닫기', 'F1 / Esc']].map(([a, b]) => `<tr><td>${a}</td><td><kbd>${b}</kbd></td></tr>`).join('')}</table>
    <div class="help-note">차트 위: 휠 줌 · 드래그 이동 · <b>오른쪽 드래그 영역 확대</b>(<kbd>Shift</kbd> = X만, <kbd>Ctrl</kbd> = Y만) · 더블클릭 전체 · 클릭 커서 A · <kbd>Shift</kbd>+클릭 커서 B · 우클릭 커서 해제</div>` },
  { id: 'tips', label: '활용 팁', html: () => `
    <h3>활용 팁</h3>
    <div class="help-card"><b>큰 파일</b><p>1 GB TDMS도 메모리 맵으로 수 ms에 열립니다. 채널마다 min/max 피라미드를 만들어 파일 옆 <code>.nvcache</code>에 저장하니 두 번째부터는 바로 그려져요.</p></div>
    <div class="help-card"><b>로그 여러 개 비교</b><p>NAVIGATOR에서 파일을 <kbd>Ctrl</kbd>-클릭으로 고르고 COMPARE → 공통 채널을 파일별 곡선으로 겹쳐 봅니다. 시작 시각으로 정렬하거나 0 s 기준, 수동 오프셋(ms)도 돼요.</p></div>
    <div class="help-card"><b>테마</b><p>THEME(라이트/다크)와 PLOT(플롯 밝기)은 따로 바꿀 수 있어요. 창 크기·테마·모드는 다음 실행에 복원되고, 파일·차트는 다시 열지 않습니다.</p></div>
    <div class="help-card"><b>레이아웃 파일</b><p>.tdv(VIEW 레이아웃)를 열면 차트·테이블이 그대로 재현됩니다.</p></div>` },
];

export class Help {
  private S: State | null = null; private autoDone = false; private tour: Tour | null = null;
  setState(s: State) {
    this.S = s; const app = (s as any).app;
    if (!this.autoDone && app && app.tourSeen === false && !app.automation) { this.autoDone = true; setTimeout(() => { if (!this.tour && !document.querySelector('.modal-bg')) this.startTour(0); }, 1200); }
  }
  get tourActive() { return !!this.tour; }
  show(section = 'start') {
    document.querySelector('.help-bg')?.remove();
    const app = (this.S as any)?.app ?? {};
    const legacy = !!(this.S as any)?.legacyTabs;
    const sections = SECTIONS.filter(s => legacy || (s.id !== 'analysis' && s.id !== 'report'));
    const dlg = h(`<div class="modal-bg help-bg"><div class="modal help" role="dialog" aria-label="사용 가이드"><div class="help-h"><b>Nexys VIEW 사용 가이드</b><button class="xbtn" data-h="close" aria-label="닫기">${I.close()}</button></div>
      <div class="help-body"><nav class="help-nav" role="tablist">${sections.map(s => `<button role="tab" data-s="${s.id}">${s.label}</button>`).join('')}<span class="help-ver">${esc(app.versionText || '')}</span></nav><div class="help-pane"></div></div></div></div>`);
    const pane = dlg.querySelector('.help-pane') as HTMLElement;
    const select = (id: string) => { const s = sections.find(x => x.id === id) ?? sections[0]; pane.innerHTML = s.html(app); pane.scrollTop = 0; dlg.querySelectorAll('[data-s]').forEach(b => b.classList.toggle('on', (b as HTMLElement).dataset.s === s.id)); };
    dlg.addEventListener('click', e => {
      const t = e.target as HTMLElement;
      const u = (t.closest('[data-url]') as HTMLElement | null)?.dataset.url; if (u) { e.preventDefault(); bridge.call('openUrl', { url: u }); return; }
      const s = (t.closest('[data-s]') as HTMLElement | null)?.dataset.s; if (s) { select(s); return; }
      const a = (t.closest('[data-h]') as HTMLElement | null)?.dataset.h;
      if (a === 'close' || t === dlg) dlg.remove(); else if (a === 'tour') { dlg.remove(); this.startTour(0); }
    });
    document.body.append(dlg); select(section);
  }
  startTour(step = 0) {
    document.querySelector('.help-bg')?.remove(); this.tour?.finish();
    this.tour = new Tour(TOUR_STEPS, m => bridge.call('setMode', { mode: m }), () => this.S, () => { this.tour = null; bridge.call('tourSeen'); });
    return this.tour.go(step);
  }
}
