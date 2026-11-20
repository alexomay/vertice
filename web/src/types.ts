// Lo que manda el motor (mismos nombres que los DTO de Vertice.Core/Engine.cs, en camelCase)

export type LngLat = [number, number]

export interface RouteDto { vehicleId: number; code: string; stops: number; km: number; kg: number; endsAt: number; path: LngLat[] }
export interface PlanSummary { km: number; vans: number; lateStops: number; lateMin: number; cost: number; endsAt: number; routes: RouteDto[] }
export interface ProgressPoint { it: number; ms: number; best: number }
export interface PlanCompare { manual: PlanSummary; optimized: PlanSummary | null; iterations: number; ms: number; progress: ProgressPoint[] }

export interface Kpis {
  total: number; delivered: number; onTime: number; late: number; onTimePct: number; atRisk: number; predictedLate: number; unassigned: number
  kmDriven: number; kmPlanned: number; kmManual: number; vansPlanned: number; vansManual: number; latePlanned: number; lateManual: number
  costPlanned: number; costManual: number; fuelSavedL: number; fuelSavedMxn: number; co2SavedKg: number; savingsPct: number
}

export interface UnitDto {
  id: number; code: string; driver: string; status: string; lat: number; lon: number; done: number; pending: number; km: number
  loadPct: number; next: string | null; nextCustomer: string | null; nextEta: number | null; risk: 'ok' | 'riesgo' | 'tarde'; received: number; path: LngLat[]
}

export type OrderState = 'pendiente' | 'entregado' | 'tarde' | 'riesgo' | 'atrasado' | 'sinunidad'
export interface OrderDto {
  id: number; code: string; customer: string; area: string; lat: number; lon: number; open: number; close: number; kg: number
  urgent: boolean; state: OrderState; vehicleId: number | null; eta: number | null; deliveredAt: number | null; lateMin: number | null
}
export interface IncidentDto { id: number; label: string; lat: number; lon: number; radiusKm: number; until: number; active: boolean }
export interface LogDto { at: number; kind: string; text: string; vehicleId: number | null }
export interface ReplanDto { at: number; reason: string; moved: number; units: string[]; riskBefore: number; riskAfter: number; ms: number; iterations: number }

export interface Snapshot {
  phase: 'plan' | 'ready' | 'live'; now: number; planVersion: number; finished: boolean
  depot: { name: string; lat: number; lon: number }
  units: UnitDto[]; orders: OrderDto[]; incidents: IncidentDto[]; kpis: Kpis; log: LogDto[]; lastReplan: ReplanDto | null
}

export interface Answer { question: string; answer: string; at: number }

/** Colores de ruta por unidad: distinguibles sobre el mapa oscuro. */
export const UNIT_COLORS = ['#ffb020', '#4da3ff', '#2ecc9a', '#ff6b8b', '#b38cff', '#ff8a3d', '#3dd6e0', '#c6e05a', '#f5d547', '#7aa2ff', '#ff5ad1', '#8fe3b0', '#e0a96d', '#9ec5ff', '#ff9f9f', '#a3a9ff']
export const unitColor = (id: number) => UNIT_COLORS[id % UNIT_COLORS.length]

export const hm = (m: number | null | undefined) => {
  if (m == null || !isFinite(m)) return '—'
  const t = Math.round(m)
  return `${String(Math.floor(t / 60) % 24).padStart(2, '0')}:${String(t % 60).padStart(2, '0')}`
}
export const money = (n: number) => '$' + Math.round(n).toLocaleString('es-MX')
export const num = (n: number, d = 0) => n.toLocaleString('es-MX', { maximumFractionDigits: d, minimumFractionDigits: d })
