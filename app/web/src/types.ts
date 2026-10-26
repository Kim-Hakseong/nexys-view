// Snapshot shapes produced by Services/Engine.Snapshot() (camelCase JSON).
export interface ChannelDto { id: string; name: string; unit: string; length: number; isText: boolean; step: boolean; time: boolean; ready: boolean; }
export interface GroupDto { name: string; channels: ChannelDto[]; }
export interface FileDto {
  name: string; path: string; dir: string; format: string; writer: string; channelCount: number; samples: number; sizeBytes: number; openMs: number;
  start: number | null; startText: string; duration: number | null; rate: number | null; timeChannel: string | null; hier: boolean; segments: string | null; warnings: number;
  groups: GroupDto[]; props: { k: string; v: string }[];
}
export interface CurveDto {
  idx: number; channelId: string; label: string; name: string; unit: string; color: number; hex: string; visible: boolean; step: boolean; lane: boolean; right: boolean; axisUnit: string;
  xChannel: string | null; xMono: boolean; xOffset: number; length: number; file: string; ready: boolean; dataType: string;
  width: number; points: boolean; axisId: string;
}
export interface CursorDto { on: boolean; x: number; label: string; }
export interface AxisDto { id: string; unit: string; right: boolean; auto: boolean; min: number; max: number; log: boolean; curves: number; }
export interface BandDto { on: boolean; x0: number; x1: number; label0: string; label1: string; }
export interface ChartDto {
  id: number; title: string; xmin: number; xmax: number; xUnit: string; xTimeMode: string; xTimeOrigin: number; lanes: boolean; laneCount: number;
  alignMode: string; alignOffsetMs: number; grid: boolean; legend: boolean; isXY: boolean; cursorA: CursorDto; cursorB: CursorDto;
  xChannel: { id: string; name: string; unit: string } | null; suggestedX: { id: string; name: string; unit: string } | null; curves: CurveDto[];
  // 0.9.3
  cursorMode: string; band: BandDto; linkX: boolean; linkKey: string | null; detailOf: number | null; detailOfName: string | null; hasDetail: boolean; zoomHistory: number; axes: AxisDto[];
  pageId: number; windowId: number;
}
export interface PropsDto {
  id: string; name: string; group: string; file: string; unit: string; dataType: string; length: number; isText: boolean; xKind: string; xStart: number; xIncrement: number; xUnit: string;
  rate: number | null; render: string; timeCandidate: boolean; absolute: number | null; absoluteText: string | null; min: number | null; max: number | null; ready: boolean;
  value: number | null; valueB: number | null; valueLabel: string; valueWhen: string; band: number[] | null; xChannel: string | null; display: string; wfStart: string | null;
  inChart: number | null; curveIdx: number | null; props: { k: string; v: string }[]; description: string;
}
export interface AnalysisDto {
  functions: { name: string; meta: string }[]; function: string; schema: { key: string; label: string; unit: string; def: string; options: string[] | null }[];
  params: Record<string, string>; inputs: { id: string; name: string; length: number }[]; running: boolean; notes: string[];
  results: { id: string; name: string; unit: string; length: number; meta: string }[]; stats: any; lastRunMs: number; subtitle: string; validity: { ok: boolean; text: string }; hasPreview: boolean; canShow: boolean;
}
export interface ReportDto { version: number; blocks: { idx: number; kind: string; text: string; chartId: number; name: string; summary: string }[]; charts: { id: number; name: string }[]; }
export interface PageDto { id: number; name: string; columns: number; charts: number; }
export interface WindowDto { id: number; title: string; charts: number; }
export interface TableDto { id: number; pageId: number; channels: { id: string; name: string; unit: string; length: number }[]; rows: number; cursorRow: number | null; }
export interface State {
  theme: string; plot: string; mode: string; developer: boolean; legacyTabs: boolean; dropMode: string;
  pages: PageDto[]; activePage: number; autoPage: { on: boolean; n: number }; windows: WindowDto[]; status: { text: string; led: string }; undo: { can: boolean; name: string | null; canRedo: boolean };
  files: FileDto[]; charts: ChartDto[]; sparks: Record<string, number[]>; activeChart: number; selected: string | null; selectedFile: string | null; selectedFiles: string[];
  align: { mode: string; offsetMs: number }; common: string[]; props: PropsDto | null; fileProps: FileDto | null; analysis: AnalysisDto; report: ReportDto; preview: ChartDto | null;
  tables: TableDto[]; cache: { ready: number; total: number; mb: number };
}
// frame header from Services/FrameServer
export interface AxisHdr { id: string; right: boolean; unit: string; min: number; max: number; log: boolean; color: number | null; ticks: { v: number; label: string }[]; }
export interface CurveHdr { idx: number; mode: 'env' | 'raw' | 'xy' | 'lane'; off: number; n: number; color: number; axis?: string; step?: boolean; label: string; width?: number; points?: boolean; }
export interface BandReadout { idx: number; label: string; unit: string; color: number; min: number | null; max: number | null; mean: number | null; rms: number | null; pp: number | null; n: number | null; approx: boolean; }
export interface FrameHdr {
  chart: number; xmin: number; xmax: number; xunit: string; xTimeMode: string; columns: number; axes: AxisHdr[]; xticks: { v: number; label: string }[]; curves: CurveHdr[];
  cursors: { a: { x: number; label: string } | null; b: { x: number; label: string } | null; dx: number | null; dxLabel: string; readouts: { idx: number; label: string; unit: string; color: number; a: number | null; b: number | null; delta: number | null; seg: { min: number; max: number; mean: number } | null }[] } | null;
  band: { x0: number; x1: number; label0: string; label1: string; lenLabel: string; readouts: BandReadout[] } | null;
  bandRef: { x0: number; x1: number } | null; cursorMode: string;
  lanes: boolean; stats: { ms: number; samples: number; columns: number; level: string; floats: number };
}
