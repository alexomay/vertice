import * as signalR from '@microsoft/signalr'
import type { Answer, PlanCompare, Snapshot } from './types'

export type Progress = { it: number; ms: number; best: number }

/** Lo que la interfaz necesita del motor; hay dos: en el navegador (WebAssembly) o en el servidor (SignalR). */
export interface EngineClient {
  readonly mode: 'wasm' | 'server'
  boot(): Promise<number>
  create(seed: number, orders: number, vehicles: number): Promise<PlanCompare>
  optimize(ms: number, onProgress: (p: Progress) => void): Promise<PlanCompare>
  start(guided: boolean): Promise<void>
  advance(minutes: number): Promise<Snapshot>
  snapshot(): Promise<Snapshot>
  breakdown(vehicleId: number): Promise<Snapshot>
  urgent(lat?: number, lon?: number): Promise<Snapshot>
  traffic(lat?: number, lon?: number): Promise<Snapshot>
  replan(): Promise<Snapshot>
  setAutoReplan(on: boolean): Promise<void>
  ask(q: string): Promise<Answer>
}

/** Motor .NET corriendo en un Web Worker dentro del navegador. */
export class WasmEngine implements EngineClient {
  readonly mode = 'wasm' as const
  private worker = new Worker(`${import.meta.env.BASE_URL}engine-worker.js`, { type: 'module' })
  private seq = 0
  private pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>()
  private onProgress: ((p: Progress) => void) | null = null

  constructor() {
    this.worker.onmessage = (e) => {
      const d = e.data
      if (d.type === 'progress') { this.onProgress?.(d); return }
      const p = this.pending.get(d.id)
      if (!p) return
      this.pending.delete(d.id)
      if (d.ok) p.resolve(d.result)
      else p.reject(new Error(d.error))
    }
  }

  private call<T>(op: string, ...args: unknown[]): Promise<T> {
    const id = ++this.seq
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject })
      this.worker.postMessage({ id, op, args })
    })
  }

  private json<T>(op: string, ...args: unknown[]) { return this.call<string>(op, ...args).then((s) => JSON.parse(s) as T) }

  boot() { return this.call<number>('boot') }
  create(seed: number, orders: number, vehicles: number) { return this.json<PlanCompare>('Create', seed, orders, vehicles) }
  async optimize(ms: number, onProgress: (p: Progress) => void) {
    this.onProgress = onProgress
    try { return await this.json<PlanCompare>('Optimize', ms) } finally { this.onProgress = null }
  }
  start(guided: boolean) { return this.call<void>('Start', guided) }
  advance(minutes: number) { return this.json<Snapshot>('Advance', minutes) }
  snapshot() { return this.json<Snapshot>('Snapshot') }
  breakdown(vehicleId: number) { return this.json<Snapshot>('Breakdown', vehicleId) }
  urgent(lat = NaN, lon = NaN) { return this.json<Snapshot>('Urgent', lat, lon) }
  traffic(lat = NaN, lon = NaN) { return this.json<Snapshot>('Traffic', lat, lon) }
  replan() { return this.json<Snapshot>('Replan') }
  setAutoReplan(on: boolean) { return this.call<void>('SetAutoReplan', on) }
  ask(q: string) { return this.json<Answer>('Ask', q) }
}

/** Motor en el servidor ASP.NET Core: mismas operaciones por un hub de SignalR (una sesión por conexión). */
export class ServerEngine implements EngineClient {
  readonly mode = 'server' as const
  private conn = new signalR.HubConnectionBuilder().withUrl(`${import.meta.env.BASE_URL}hub/ops`).withAutomaticReconnect().build()
  private onProgress: ((p: Progress) => void) | null = null

  constructor() {
    this.conn.on('progress', (it: number, ms: number, best: number) => this.onProgress?.({ it, ms, best }))
  }

  private json<T>(method: string, ...args: unknown[]) { return this.conn.invoke<string>(method, ...args).then((s) => JSON.parse(s) as T) }

  async boot() { const t = performance.now(); await this.conn.start(); return Math.round(performance.now() - t) }
  create(seed: number, orders: number, vehicles: number) { return this.json<PlanCompare>('Create', seed, orders, vehicles) }
  async optimize(ms: number, onProgress: (p: Progress) => void) {
    this.onProgress = onProgress
    try { return await this.json<PlanCompare>('Optimize', ms) } finally { this.onProgress = null }
  }
  start(guided: boolean) { return this.conn.invoke<void>('Start', guided) }
  advance(minutes: number) { return this.json<Snapshot>('Advance', minutes) }
  snapshot() { return this.json<Snapshot>('Snapshot') }
  breakdown(vehicleId: number) { return this.json<Snapshot>('Breakdown', vehicleId) }
  urgent(lat?: number, lon?: number) { return this.json<Snapshot>('Urgent', lat ?? null, lon ?? null) }
  traffic(lat?: number, lon?: number) { return this.json<Snapshot>('Traffic', lat ?? null, lon ?? null) }
  replan() { return this.json<Snapshot>('Replan') }
  setAutoReplan(on: boolean) { return this.conn.invoke<void>('SetAutoReplan', on) }
  ask(q: string) { return this.json<Answer>('Ask', q) }
}

/**
 * ?motor=servidor usa la API; por omisión todo corre en el navegador.
 * La imagen de Docker se compila con VITE_ENGINE=server (sirve la API y no lleva el motor WebAssembly).
 */
export function createEngine(): EngineClient {
  const mode = new URLSearchParams(location.search).get('motor')
  const server = mode === 'servidor' || (import.meta.env.VITE_ENGINE === 'server' && mode !== 'navegador')
  return server ? new ServerEngine() : new WasmEngine()
}
