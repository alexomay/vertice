import { useCallback, useEffect, useRef, useState } from 'react'
import { ArrowsClockwise, Lightning, Pause, Play, TrafficCone, Wrench, X, ChatCircleText, ListBullets, GithubLogo, Truck } from '@phosphor-icons/react'
import { createEngine } from './engine'
import type { PlanCompare, ProgressPoint, Snapshot } from './types'
import { hm, money, num } from './types'
import MapView from './components/MapView'
import { AssistantPanel, Feed, KpiStrip, Logo, PlanPanel, UnitList } from './components/Panels'

const engine = createEngine()
let booting: Promise<number> | null = null   // StrictMode monta dos veces: el motor se carga una sola
const SEED = 2026, ORDERS = 150, FLEET = 12
const SPEEDS = [2, 5, 10, 20]   // minutos simulados por segundo real
const SUGGESTIONS = ['¿Qué entregas están en riesgo?', '¿Por qué va tarde la unidad más retrasada?', '¿Cuánto llevamos ahorrado?', '¿Cómo va el día?']

export default function App() {
  const [bootMs, setBootMs] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [plan, setPlan] = useState<PlanCompare | null>(null)
  const [snap, setSnap] = useState<Snapshot | null>(null)
  const [optimizing, setOptimizing] = useState(false)
  const [progress, setProgress] = useState<ProgressPoint[]>([])
  const [view, setView] = useState<'manual' | 'optimized'>('manual')
  const [running, setRunning] = useState(false)
  const [speed, setSpeed] = useState(5)
  const [selected, setSelected] = useState<number | null>(null)
  const [pick, setPick] = useState<null | 'urgent' | 'traffic'>(null)
  const [tab, setTab] = useState<'eventos' | 'asistente'>('eventos')
  const [onlyImportant, setOnlyImportant] = useState(true)
  const [auto, setAuto] = useState(true)
  const [toast, setToast] = useState<Snapshot['lastReplan']>(null)
  const [busy, setBusy] = useState(false)
  const seenReplan = useRef<number | null>(null)

  // ── arranque: carga el motor y arma el día ──
  useEffect(() => {
    booting ??= engine.boot()
    booting
      .then(async (ms) => {
        setBootMs(ms)
        setPlan(await engine.create(SEED, ORDERS, FLEET))
        setSnap(await engine.snapshot())
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [])

  const optimize = async () => {
    setOptimizing(true)
    setProgress([])
    const buf: ProgressPoint[] = []
    let last = 0
    try {
      const res = await engine.optimize(6000, (p) => {
        buf.push({ it: p.it, ms: p.ms, best: p.best })
        const now = performance.now()
        if (now - last > 80) { last = now; setProgress([...buf]) }
      })
      setPlan(res)
      setView('optimized')
      setSnap(await engine.snapshot())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setOptimizing(false)
    }
  }

  const start = async () => {
    await engine.start(true)
    setSnap(await engine.snapshot())
    setRunning(true)
  }

  // ── reloj de la operación: pide el siguiente estado cuando llega el anterior ──
  const speedRef = useRef(speed)
  speedRef.current = speed
  useEffect(() => {
    if (!running) return
    let alive = true
    let prev = performance.now()
    ;(async () => {
      while (alive) {
        const now = performance.now()
        const dt = Math.min(0.5, (now - prev) / 1000)
        prev = now
        const s = await engine.advance(dt * speedRef.current)
        if (!alive) break
        setSnap(s)
        if (s.finished) { setRunning(false); break }
        await new Promise((r) => setTimeout(r, 90))
      }
    })().catch((e) => setError(String(e)))
    return () => { alive = false }
  }, [running])

  // aviso cuando se re-optimiza
  useEffect(() => {
    const r = snap?.lastReplan
    if (!r || r.at === seenReplan.current) return
    seenReplan.current = r.at
    setToast(r)
    const t = setTimeout(() => setToast(null), 7000)
    return () => clearTimeout(t)
  }, [snap?.lastReplan])

  const act = useCallback(async (fn: () => Promise<Snapshot>) => {
    setBusy(true)
    try { setSnap(await fn()) } catch (e) { setError(String(e)) } finally { setBusy(false) }
  }, [])

  const onPick = (lat: number, lon: number) => {
    const p = pick
    setPick(null)
    if (p === 'urgent') act(() => engine.urgent(lat, lon))
    if (p === 'traffic') act(() => engine.traffic(lat, lon))
  }

  const live = snap?.phase === 'live'
  const planRoutes = plan ? (view === 'manual' || !plan.optimized ? plan.manual.routes : plan.optimized.routes) : null

  if (error) {
    return (
      <div className="grid h-full place-items-center p-6 text-center">
        <div className="max-w-md">
          <Logo className="mx-auto h-12 w-12" />
          <p className="mt-4 font-semibold">No se pudo iniciar Vértice</p>
          <p className="mt-2 font-mono text-xs text-muted">{error}</p>
          <button type="button" onClick={() => location.reload()} className="mt-5 rounded-xl border border-line px-4 py-2 text-sm">Reintentar</button>
        </div>
      </div>
    )
  }

  return (
    <div className="flex min-h-full flex-col lg:grid lg:h-full lg:grid-rows-[56px_1fr]">
      {/* ── barra superior ── */}
      <header className="sticky top-0 z-20 flex h-[56px] shrink-0 items-center gap-4 border-b border-line bg-panel px-4 lg:static lg:px-5">
        <div className="flex items-center gap-2.5">
          <Logo className="h-8 w-8" />
          <div className="leading-none">
            <p className="text-[17px] font-semibold tracking-[-0.02em]">Vértice</p>
            <p className="mt-0.5 text-[11px] text-muted max-sm:hidden">Torre de control logística</p>
          </div>
        </div>
        <div className="mx-auto flex items-center gap-3">
          <span className="tnum font-mono text-[22px] font-semibold tracking-tight">{hm(snap?.now ?? 470)}</span>
          {live && (
            <div className="flex items-center gap-1 rounded-xl border border-line bg-bg p-1">
              <button type="button" onClick={() => setRunning((r) => !r)} disabled={snap?.finished} className="grid h-8 w-8 place-items-center rounded-lg bg-panel-2 hover:text-accent" aria-label={running ? 'Pausar' : 'Continuar'}>
                {running ? <Pause size={16} weight="fill" /> : <Play size={16} weight="fill" />}
              </button>
              {SPEEDS.map((s) => (
                <button key={s} type="button" onClick={() => setSpeed(s)} className={`tnum h-8 rounded-lg px-2 font-mono text-[11px] ${speed === s ? 'bg-panel-2 text-accent' : 'text-muted hover:text-ink'}`} title={`${s} minutos por segundo`}>{s}×</button>
              ))}
            </div>
          )}
        </div>
        <a href="https://github.com/alexomay/vertice" target="_blank" rel="noopener noreferrer" className="hidden items-center gap-1.5 text-xs text-muted hover:text-ink md:flex"><GithubLogo size={16} /> Código</a>
      </header>

      <div className={`relative flex flex-col lg:grid lg:min-h-0 lg:grid-rows-[minmax(0,1fr)] ${live ? 'lg:grid-cols-[360px_1fr_360px]' : 'lg:grid-cols-[360px_1fr]'}`}>
        {/* ── panel izquierdo ── */}
        <aside className="border-line bg-panel max-lg:order-2 max-lg:border-t lg:min-h-0 lg:border-r">
          {!live ? (
            <PlanPanel plan={plan} optimizing={optimizing} live={progress} view={view} setView={setView} onOptimize={optimize} onStart={start} bootMs={bootMs} mode={engine.mode} fleet={FLEET} />
          ) : (
            <div className="flex flex-col p-4 lg:h-full lg:min-h-0">
              <KpiStrip k={snap!.kpis} />
              <p className="mb-2 mt-4 flex items-center justify-between text-xs text-muted"><span>Unidades</span><span>{snap!.units.filter((u) => u.status !== 'terminó' && u.status !== 'descompuesta' && (u.pending > 0 || u.status !== 'en CEDIS')).length} en la calle</span></p>
              <div className="thin -mx-1 px-1 lg:min-h-0 lg:flex-1 lg:overflow-y-auto"><UnitList units={snap!.units} selected={selected} onSelect={setSelected} /></div>
            </div>
          )}
        </aside>

        {/* ── mapa ── */}
        <main className="relative h-[58vh] max-lg:order-1 lg:h-auto lg:min-h-0">
          <MapView snapshot={snap} planRoutes={live ? null : planRoutes} planStyle={view} selected={selected} onSelect={setSelected} pick={pick} onPick={onPick} />

          {!snap && (
            <div className="absolute inset-0 grid place-items-center bg-bg/70 backdrop-blur-sm">
              <div className="text-center">
                <Logo className="mx-auto h-12 w-12 animate-pulse" />
                <p className="mt-3 text-sm text-muted">Cargando el motor .NET en tu navegador…</p>
              </div>
            </div>
          )}

          {pick && (
            <div className="absolute inset-x-0 top-4 mx-auto w-fit rounded-xl border border-accent/50 bg-panel px-4 py-2 text-sm shadow-2xl vx-in">
              Toca el mapa para {pick === 'urgent' ? 'ubicar el pedido urgente' : 'marcar dónde está el tráfico'}
              <button type="button" onClick={() => setPick(null)} className="ml-3 text-muted hover:text-ink" aria-label="Cancelar"><X size={14} /></button>
            </div>
          )}

          {toast && (
            <div className="absolute left-1/2 top-4 w-[min(92%,520px)] -translate-x-1/2 rounded-xl border border-accent/40 bg-panel/95 p-3.5 shadow-2xl backdrop-blur vx-in">
              <p className="flex items-center gap-2 text-sm font-semibold"><ArrowsClockwise size={16} className="text-accent" /> Re-optimizado en {(toast.ms / 1000).toFixed(1)} s · {num(toast.iterations)} iteraciones</p>
              <p className="mt-1 text-[13px] text-muted">
                Por {toast.reason}: {toast.moved} entrega{toast.moved === 1 ? '' : 's'} reasignada{toast.moved === 1 ? '' : 's'}{toast.units.length ? ` a ${toast.units.join(', ')}` : ''}. Entregas en riesgo: <span className="text-ink">{toast.riskBefore} → {toast.riskAfter}</span>
              </p>
            </div>
          )}

          {/* acciones de la operación */}
          {live && !snap?.finished && (
            <div className="absolute bottom-5 left-1/2 flex -translate-x-1/2 flex-wrap items-center justify-center gap-1.5 rounded-2xl border border-line bg-panel/95 p-1.5 shadow-2xl backdrop-blur max-sm:bottom-3 max-sm:w-[94%]">
              <ActionBtn icon={Wrench} label={selected != null ? `Descomponer U-${String(selected + 1).padStart(2, '0')}` : 'Descompostura'} disabled={busy} onClick={() => act(() => engine.breakdown(selected ?? -1))} />
              <ActionBtn icon={Lightning} label="Pedido urgente" active={pick === 'urgent'} disabled={busy} onClick={() => setPick(pick === 'urgent' ? null : 'urgent')} />
              <ActionBtn icon={TrafficCone} label="Tráfico" active={pick === 'traffic'} disabled={busy} onClick={() => setPick(pick === 'traffic' ? null : 'traffic')} />
              <span className="mx-1 h-6 w-px bg-line max-sm:hidden" />
              <ActionBtn icon={ArrowsClockwise} label="Re-optimizar" primary disabled={busy} onClick={() => act(() => engine.replan())} />
              <label className="flex cursor-pointer items-center gap-1.5 px-2 text-[11px] text-muted" title="Re-optimiza solo al descomponerse una unidad o entrar un pedido urgente">
                <input type="checkbox" checked={auto} onChange={(e) => { setAuto(e.target.checked); engine.setAutoReplan(e.target.checked) }} className="accent-[#ffb020]" /> Automático
              </label>
            </div>
          )}

          {snap?.finished && <DaySummary snap={snap} />}
        </main>

        {/* ── panel derecho: eventos y asistente ── */}
        <aside className={`flex-col border-line bg-panel p-4 ${live ? 'flex' : 'hidden'} max-lg:order-3 max-lg:h-[80vh] max-lg:border-t lg:min-h-0 lg:border-l`}>
          <div className="mb-3 flex rounded-xl bg-bg p-1" role="tablist">
            {([['eventos', 'Eventos', ListBullets], ['asistente', 'Asistente', ChatCircleText]] as const).map(([id, label, Icon]) => (
              <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)} className={`flex flex-1 items-center justify-center gap-1.5 rounded-lg py-2 text-[13px] ${tab === id ? 'bg-panel-2 font-medium text-ink' : 'text-muted hover:text-ink'}`}>
                <Icon size={15} weight={tab === id ? 'fill' : 'regular'} /> {label}
              </button>
            ))}
          </div>
          {tab === 'eventos' ? (
            <>
              <label className="mb-2 flex cursor-pointer items-center gap-2 px-1 text-[11px] text-muted"><input type="checkbox" checked={onlyImportant} onChange={(e) => setOnlyImportant(e.target.checked)} className="accent-[#ffb020]" /> Solo lo importante</label>
              <div className="thin min-h-0 flex-1 overflow-y-auto">{snap && <Feed log={snap.log} onlyImportant={onlyImportant} />}</div>
            </>
          ) : (
            <AssistantPanel ask={(q) => engine.ask(q)} suggestions={SUGGESTIONS} disabled={!live} />
          )}
        </aside>
      </div>
    </div>
  )
}

function ActionBtn({ icon: Icon, label, onClick, disabled, active, primary }: { icon: typeof Truck; label: string; onClick: () => void; disabled?: boolean; active?: boolean; primary?: boolean }) {
  return (
    <button type="button" onClick={onClick} disabled={disabled}
      className={`flex h-10 items-center gap-1.5 rounded-xl px-3 text-[13px] font-medium transition disabled:opacity-50 ${primary ? 'bg-accent text-bg hover:brightness-110' : active ? 'bg-accent/15 text-accent' : 'text-ink hover:bg-panel-2'}`}>
      <Icon size={16} weight={primary || active ? 'fill' : 'regular'} /> <span className="max-sm:hidden">{label}</span>
    </button>
  )
}

function DaySummary({ snap }: { snap: Snapshot }) {
  const k = snap.kpis
  return (
    <div className="absolute inset-0 grid place-items-center bg-bg/60 p-4 backdrop-blur-sm">
      <div className="w-full max-w-md rounded-2xl border border-line bg-panel p-6 shadow-2xl vx-in">
        <p className="font-mono text-[11px] uppercase tracking-[.14em] text-accent">Fin del día · {hm(snap.now)}</p>
        <h2 className="mt-2 text-2xl font-semibold tracking-[-0.02em]">{k.delivered} de {k.total} entregas, {num(k.onTimePct)}% a tiempo</h2>
        <p className="mt-2 text-sm leading-relaxed text-muted">Con {snap.log.filter((l) => l.kind === 'Averia').length} descompostura(s), {snap.log.filter((l) => l.kind === 'Urgente').length} pedidos urgentes y {snap.log.filter((l) => l.kind === 'Trafico').length} incidentes de tráfico resueltos en vivo.</p>
        <div className="mt-5 grid grid-cols-2 gap-3 text-sm">
          <Stat label="Km recorridos" value={num(k.kmDriven)} />
          <Stat label="Re-optimizaciones" value={String(snap.log.filter((l) => l.kind === 'Reoptimizacion').length)} />
          <Stat label="Ahorro vs manual" value={money(k.costManual - k.costPlanned)} good />
          <Stat label="CO₂ evitado" value={`${num(k.co2SavedKg)} kg`} good />
        </div>
        <button type="button" onClick={() => location.reload()} className="mt-6 h-11 w-full rounded-xl bg-accent font-semibold text-bg hover:brightness-110">Volver a empezar</button>
      </div>
    </div>
  )
}

function Stat({ label, value, good }: { label: string; value: string; good?: boolean }) {
  return (
    <div className="rounded-xl border border-line px-3 py-2.5">
      <p className="text-[11px] text-muted">{label}</p>
      <p className={`tnum mt-0.5 font-mono text-[17px] font-semibold ${good ? 'text-ok' : ''}`}>{value}</p>
    </div>
  )
}
