import { useEffect, useRef, useState } from 'react'
import {
  ArrowsClockwise, Lightning, PaperPlaneRight, Path, Truck, Warning, Wrench, Package, TrafficCone, CheckCircle, Clock, Sparkle, Flag,
} from '@phosphor-icons/react'
import type { Answer, Kpis, LogDto, PlanCompare, PlanSummary, ProgressPoint, UnitDto } from '../types'
import { hm, money, num, unitColor } from '../types'

export function Logo({ className = '' }: { className?: string }) {
  return (
    <svg viewBox="0 0 64 64" className={className} aria-hidden>
      <path d="M12 16 L32 48 L52 16" fill="none" stroke="#ffb020" strokeWidth="5" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M12 16 L52 16" fill="none" stroke="#2a3846" strokeWidth="3" strokeDasharray="3 5" strokeLinecap="round" />
      <circle cx="12" cy="16" r="5.5" fill="#e8eef4" />
      <circle cx="52" cy="16" r="5.5" fill="#e8eef4" />
      <circle cx="32" cy="48" r="6.5" fill="#ffb020" />
    </svg>
  )
}

// ── Plan del día: manual contra optimizado ─────────────────────────────

function Delta({ a, b, unit = '', better = 'lower', fmt = (n: number) => num(n) }: { a: number; b: number; unit?: string; better?: 'lower' | 'higher'; fmt?: (n: number) => string }) {
  const d = b - a
  const good = better === 'lower' ? d < 0 : d > 0
  if (Math.abs(d) < 1e-9) return <span className="text-muted">igual</span>
  const pct = a !== 0 ? Math.round((Math.abs(d) / Math.abs(a)) * 100) : null
  return <span className={good ? 'text-ok' : 'text-bad'}>{d < 0 ? '−' : '+'}{fmt(Math.abs(d))}{unit}{pct != null && pct > 0 ? ` (${pct}%)` : ''}</span>
}

function Row({ label, manual, opt, delta }: { label: string; manual: string; opt: string | null; delta?: React.ReactNode }) {
  return (
    <div className="grid grid-cols-[1fr_auto_auto] items-baseline gap-x-4 border-b border-line py-2.5 text-sm last:border-0">
      <span className="text-muted">{label}</span>
      <span className="tnum font-mono text-[13px] text-muted line-through decoration-muted/40">{opt ? manual : ''}</span>
      <span className="tnum font-mono text-[13px] font-semibold">{opt ?? manual}</span>
      {opt && delta && <span className="col-span-3 -mt-0.5 text-right font-mono text-[11px]">{delta}</span>}
    </div>
  )
}

export function CostChart({ points, height = 64 }: { points: ProgressPoint[]; height?: number }) {
  if (points.length < 2) return <div style={{ height }} className="relative overflow-hidden rounded-lg bg-panel-2"><div className="absolute inset-y-0 w-1/3 bg-gradient-to-r from-transparent via-accent/15 to-transparent" style={{ animation: 'vx-scan 1.2s linear infinite' }} /></div>
  const w = 300
  const xs = points.map((p) => p.ms), ys = points.map((p) => p.best)
  const x0 = Math.min(...xs), x1 = Math.max(...xs) || 1, y0 = Math.min(...ys), y1 = Math.max(...ys)
  const X = (v: number) => ((v - x0) / (x1 - x0 || 1)) * w
  const Y = (v: number) => 6 + (1 - (v - y0) / (y1 - y0 || 1)) * (height - 12)
  const d = points.map((p, i) => `${i ? 'L' : 'M'}${X(p.ms).toFixed(1)},${Y(p.best).toFixed(1)}`).join(' ')
  return (
    <svg viewBox={`0 0 ${w} ${height}`} className="w-full rounded-lg bg-panel-2" style={{ height }} role="img" aria-label="Costo del mejor plan durante la optimización">
      <path d={`${d} L${w},${height} L0,${height} Z`} fill="url(#g)" opacity="0.35" />
      <defs><linearGradient id="g" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stopColor="#ffb020" /><stop offset="1" stopColor="#ffb020" stopOpacity="0" /></linearGradient></defs>
      <path d={d} fill="none" stroke="#ffb020" strokeWidth="2" vectorEffect="non-scaling-stroke" />
    </svg>
  )
}

type PlanProps = {
  plan: PlanCompare | null
  optimizing: boolean
  live: ProgressPoint[]
  view: 'manual' | 'optimized'
  setView: (v: 'manual' | 'optimized') => void
  onOptimize: () => void
  onStart: () => void
  bootMs: number | null
  mode: string
  fleet: number
}

export function PlanPanel({ plan, optimizing, live, view, setView, onOptimize, onStart, bootMs, mode, fleet }: PlanProps) {
  const m = plan?.manual
  const o = plan?.optimized ?? null
  const last = live[live.length - 1]
  return (
    <div className="thin flex flex-col gap-4 p-5 lg:h-full lg:overflow-y-auto">
      <div>
        <p className="font-mono text-[11px] uppercase tracking-[.14em] text-accent">Plan del día · CDMX</p>
        <h1 className="mt-1.5 text-[26px] font-semibold leading-tight tracking-[-0.02em]">{m ? `${num(m.routes.reduce((s, r) => s + r.stops, 0))} entregas, ${fleet} camionetas disponibles` : 'Armando el día…'}</h1>
        <p className="mt-2 text-sm leading-relaxed text-muted">Un despachador con experiencia reparte la ciudad por zonas. Vértice resuelve el problema completo: ventanas de horario, capacidad y tráfico según la hora.</p>
      </div>

      {m && (
        <div className="rounded-xl border border-line bg-panel-2/60 p-4">
          <div className="mb-1 flex items-center justify-between text-xs text-muted">
            <span>{o ? 'Manual → Vértice' : 'Plan manual'}</span>
            {o && (
              <div className="flex rounded-lg bg-bg p-0.5" role="radiogroup" aria-label="Ver en el mapa">
                {(['manual', 'optimized'] as const).map((v) => (
                  <button key={v} type="button" role="radio" aria-checked={view === v} onClick={() => setView(v)} className={`rounded-md px-2.5 py-1 text-[11px] font-medium ${view === v ? 'bg-panel-2 text-ink' : 'text-muted hover:text-ink'}`}>{v === 'manual' ? 'Manual' : 'Vértice'}</button>
                ))}
              </div>
            )}
          </div>
          <Row label="Kilómetros" manual={num(m.km)} opt={o && num(o.km)} delta={o && <Delta a={m.km} b={o.km} unit=" km" />} />
          <Row label="Camionetas" manual={String(m.vans)} opt={o && String(o.vans)} delta={o && <Delta a={m.vans} b={o.vans} />} />
          <Row label="Entregas tarde" manual={String(m.lateStops)} opt={o && String(o.lateStops)} delta={o && <Delta a={m.lateStops} b={o.lateStops} />} />
          <Row label="Costo operativo" manual={money(m.cost)} opt={o && money(o.cost)} delta={o && <Delta a={m.cost} b={o.cost} fmt={money} />} />
          <Row label="Última unidad regresa" manual={hm(m.endsAt)} opt={o && hm(o.endsAt)} />
        </div>
      )}

      {(optimizing || o) && (
        <div className="vx-in">
          <div className="mb-2 flex items-baseline justify-between text-xs text-muted">
            <span>{optimizing ? 'Optimizando (ALNS)…' : 'Optimización'}</span>
            <span className="tnum font-mono">{num(optimizing ? last?.it ?? 0 : plan?.iterations ?? 0)} iteraciones · {((optimizing ? last?.ms ?? 0 : plan?.ms ?? 0) / 1000).toFixed(1)} s</span>
          </div>
          <CostChart points={optimizing ? live : plan?.progress ?? []} />
          {!optimizing && o && m && (
            <p className="mt-3 text-sm leading-relaxed"><span className="font-semibold text-ok">{money(m.cost - o.cost)} menos al día</span> <span className="text-muted">≈ {money((m.cost - o.cost) * 22)} al mes con 22 días hábiles.</span></p>
          )}
        </div>
      )}

      <div className="mt-auto space-y-2.5 pt-2">
        {!o ? (
          <button type="button" onClick={onOptimize} disabled={!m || optimizing} className="flex h-12 w-full items-center justify-center gap-2 rounded-xl bg-accent font-semibold text-bg transition hover:brightness-110 disabled:opacity-60">
            <Sparkle size={18} weight="fill" /> {optimizing ? 'Optimizando…' : 'Optimizar con Vértice'}
          </button>
        ) : (
          <button type="button" onClick={onStart} className="flex h-12 w-full items-center justify-center gap-2 rounded-xl bg-accent font-semibold text-bg transition hover:brightness-110">
            <Truck size={18} weight="fill" /> Iniciar la operación del día
          </button>
        )}
        <p className="text-center text-[11px] text-muted">
          {mode === 'wasm' ? <>Motor .NET corriendo en tu navegador (WebAssembly){bootMs != null ? ` · cargó en ${(bootMs / 1000).toFixed(1)} s` : ''}</> : 'Motor .NET en el servidor (SignalR)'}
        </p>
      </div>
    </div>
  )
}

// ── En vivo ────────────────────────────────────────────────────────────

export function KpiStrip({ k }: { k: Kpis }) {
  const cells: [string, string, string?][] = [
    ['Entregadas', `${k.delivered}/${k.total}`],
    ['A tiempo', `${num(k.onTimePct)}%`, k.onTimePct >= 95 ? 'text-ok' : k.onTimePct >= 85 ? 'text-accent' : 'text-bad'],
    ['En riesgo', String(k.atRisk + k.predictedLate + k.unassigned), k.atRisk + k.predictedLate + k.unassigned ? 'text-accent' : 'text-ok'],
    ['Km', num(k.kmDriven)],
  ]
  return (
    <div>
      <div className="grid grid-cols-4 overflow-hidden rounded-xl border border-line">
        {cells.map(([l, v, c]) => (
          <div key={l} className="border-r border-line px-3 py-2.5 last:border-0">
            <p className="text-[11px] text-muted">{l}</p>
            <p className={`tnum mt-0.5 font-mono text-[17px] font-semibold ${c ?? ''}`}>{v}</p>
          </div>
        ))}
      </div>
      <p className="mt-2 text-[12px] text-muted">vs plan manual: <span className="text-ok">−{num(k.kmManual - k.kmPlanned)} km · −{k.vansManual - k.vansPlanned} camionetas · {money(k.costManual - k.costPlanned)} menos</span></p>
    </div>
  )
}

export function UnitList({ units, selected, onSelect }: { units: UnitDto[]; selected: number | null; onSelect: (id: number | null) => void }) {
  const shown = units.filter((u) => !(u.status === 'en CEDIS' && u.pending === 0 && u.done === 0))
  return (
    <ul className="space-y-1">
      {shown.map((u) => {
        const total = u.done + u.pending
        const on = selected === u.id
        return (
          <li key={u.id}>
            <button type="button" onClick={() => onSelect(on ? null : u.id)} className={`w-full rounded-xl border px-3 py-2.5 text-left transition-colors ${on ? 'border-line bg-panel-2' : 'border-transparent hover:bg-panel-2/60'}`}>
              <div className="flex items-center gap-2.5">
                <span className="h-7 w-1.5 shrink-0 rounded-full" style={{ background: u.status === 'descompuesta' ? '#ff5a5f' : unitColor(u.id) }} />
                <div className="min-w-0 flex-1">
                  <div className="flex items-baseline justify-between gap-2">
                    <span className="font-mono text-[13px] font-semibold">{u.code} <span className="font-sans font-normal text-muted">· {u.driver.split(' ')[0]}</span></span>
                    <span className={`text-[11px] ${u.status === 'descompuesta' ? 'text-bad' : u.risk === 'tarde' ? 'text-bad' : u.risk === 'riesgo' ? 'text-accent' : 'text-muted'}`}>
                      {u.status === 'descompuesta' ? 'descompuesta' : u.risk === 'tarde' ? 'llegará tarde' : u.risk === 'riesgo' ? 'en riesgo' : u.status}
                    </span>
                  </div>
                  <div className="mt-1.5 h-1 overflow-hidden rounded-full bg-line">
                    <div className="h-full rounded-full" style={{ width: `${total ? (u.done / total) * 100 : 100}%`, background: unitColor(u.id) }} />
                  </div>
                  <p className="mt-1 flex justify-between gap-2 truncate text-[11px] text-muted">
                    <span className="truncate">{u.next ? `→ ${u.next} · ${u.nextCustomer}` : u.status === 'terminó' ? 'Ruta terminada' : '—'}</span>
                    <span className="tnum shrink-0 font-mono">{u.next ? hm(u.nextEta) : ''} · {u.done}/{total}</span>
                  </p>
                </div>
              </div>
            </button>
          </li>
        )
      })}
    </ul>
  )
}

const KIND: Record<string, { icon: typeof Truck; cls: string }> = {
  Salida: { icon: Truck, cls: 'text-sky' },
  Entrega: { icon: CheckCircle, cls: 'text-ok' },
  EntregaTarde: { icon: Clock, cls: 'text-bad' },
  Averia: { icon: Wrench, cls: 'text-bad' },
  Urgente: { icon: Lightning, cls: 'text-accent' },
  Trafico: { icon: TrafficCone, cls: 'text-bad' },
  Reoptimizacion: { icon: Path, cls: 'text-accent' },
  Regreso: { icon: Flag, cls: 'text-muted' },
  Info: { icon: Package, cls: 'text-muted' },
}

export function Feed({ log, onlyImportant }: { log: LogDto[]; onlyImportant: boolean }) {
  const items = [...log].reverse().filter((l) => !onlyImportant || !['Entrega', 'Salida'].includes(l.kind))
  return (
    <ul className="space-y-0.5">
      {items.map((l, i) => {
        const k = KIND[l.kind] ?? KIND.Info
        const Icon = k.icon
        return (
          <li key={`${l.at}-${i}-${l.text.length}`} className="flex gap-2.5 rounded-lg px-2 py-2 text-[13px] leading-snug">
            <Icon size={16} weight="fill" className={`mt-0.5 shrink-0 ${k.cls}`} />
            <span className="min-w-0 flex-1"><span className="tnum mr-1.5 font-mono text-[11px] text-muted">{hm(l.at)}</span>{l.text}</span>
          </li>
        )
      })}
    </ul>
  )
}

export function AssistantPanel({ ask, suggestions, disabled }: { ask: (q: string) => Promise<Answer>; suggestions: string[]; disabled: boolean }) {
  const [items, setItems] = useState<Answer[]>([])
  const [q, setQ] = useState('')
  const [busy, setBusy] = useState(false)
  const end = useRef<HTMLDivElement>(null)
  useEffect(() => { end.current?.scrollIntoView({ behavior: 'smooth', block: 'end' }) }, [items])
  const send = async (text: string) => {
    if (!text.trim() || busy) return
    setBusy(true)
    setQ('')
    try { const a = await ask(text.trim()); setItems((x) => [...x, a]) } finally { setBusy(false) }
  }
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="thin min-h-0 flex-1 space-y-3 overflow-y-auto pr-1">
        {items.length === 0 && (
          <div className="rounded-xl border border-line p-4 text-sm text-muted">
            <p className="mb-3 text-ink">Pregúntale a la operación.</p>
            <p>Responde con los datos en vivo: por qué va tarde una unidad, qué está en riesgo, dónde va un pedido o cuánto se ha ahorrado.</p>
          </div>
        )}
        {items.map((a, i) => (
          <div key={i} className="vx-in space-y-1.5">
            <p className="ml-8 rounded-xl rounded-tr-sm bg-panel-2 px-3 py-2 text-[13px]">{a.question}</p>
            <div className="flex gap-2">
              <Logo className="mt-1 h-6 w-6 shrink-0" />
              <p className="whitespace-pre-line rounded-xl rounded-tl-sm border border-line px-3 py-2 text-[13px] leading-relaxed"><span className="tnum mr-1.5 font-mono text-[11px] text-muted">{hm(a.at)}</span>{a.answer}</p>
            </div>
          </div>
        ))}
        <div ref={end} />
      </div>
      <div className="mt-3 space-y-1">
        {suggestions.map((s) => (
          <button key={s} type="button" disabled={disabled || busy} onClick={() => send(s)} className="block w-full truncate rounded-lg px-2.5 py-1.5 text-left text-[12px] text-muted transition-colors hover:bg-panel-2 hover:text-ink disabled:opacity-50">{s}</button>
        ))}
      </div>
      <form onSubmit={(e) => { e.preventDefault(); send(q) }} className="mt-2 flex gap-2">
        <input value={q} onChange={(e) => setQ(e.target.value)} disabled={disabled} placeholder="¿Por qué va tarde la U-07?" className="h-10 min-w-0 flex-1 rounded-xl border border-line bg-bg px-3 text-[13px] outline-none placeholder:text-muted/60 focus:border-accent/60" />
        <button type="submit" disabled={disabled || busy || !q.trim()} className="grid h-10 w-10 place-items-center rounded-xl bg-accent text-bg disabled:opacity-50" aria-label="Preguntar"><PaperPlaneRight size={17} weight="fill" /></button>
      </form>
    </div>
  )
}

export { ArrowsClockwise, Lightning, TrafficCone, Wrench, Warning }
export type { PlanSummary }
