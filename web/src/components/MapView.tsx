import { useEffect, useRef } from 'react'
import maplibregl, { type GeoJSONSource, type Map as MlMap } from 'maplibre-gl'
import 'maplibre-gl/dist/maplibre-gl.css'
import type { IncidentDto, LngLat, OrderDto, RouteDto, Snapshot, UnitDto } from '../types'
import { hm, unitColor } from '../types'

// Mapa base oscuro de CARTO (OpenStreetMap): gratis con atribución
const STYLE = 'https://basemaps.cartocdn.com/gl/dark-matter-gl-style/style.json'
const CDMX: LngLat = [-99.15, 19.41]

type Props = {
  snapshot: Snapshot | null
  planRoutes: RouteDto[] | null      // fase de plan: rutas manuales u optimizadas
  planStyle: 'manual' | 'optimized'
  selected: number | null
  onSelect: (id: number | null) => void
  pick: null | 'urgent' | 'traffic'
  onPick: (lat: number, lon: number) => void
}

const fc = (features: GeoJSON.Feature[]): GeoJSON.FeatureCollection => ({ type: 'FeatureCollection', features })

function circle(lat: number, lon: number, km: number): LngLat[] {
  const pts: LngLat[] = []
  for (let i = 0; i <= 48; i++) {
    const a = (i / 48) * Math.PI * 2
    pts.push([lon + (km / (111.32 * Math.cos((lat * Math.PI) / 180))) * Math.cos(a), lat + (km / 110.57) * Math.sin(a)])
  }
  return pts
}

function orderColor(o: OrderDto) {
  if (o.state === 'entregado') return '#3b4a57'
  if (o.state === 'tarde') return '#ff5a5f'
  if (o.state === 'sinunidad') return '#ffffff'
  return o.vehicleId != null ? unitColor(o.vehicleId) : '#8b98a8'
}

function orderRing(o: OrderDto) {
  if (o.state === 'atrasado' || o.state === 'sinunidad') return '#ff5a5f'
  if (o.state === 'riesgo') return '#ffb020'
  return '#0a0f14'
}

export default function MapView({ snapshot, planRoutes, planStyle, selected, onSelect, pick, onPick }: Props) {
  const box = useRef<HTMLDivElement>(null)
  const map = useRef<MlMap | null>(null)
  const ready = useRef(false)
  const markers = useRef(new Map<number, maplibregl.Marker>())
  const depotMarker = useRef<maplibregl.Marker | null>(null)
  const popup = useRef(new maplibregl.Popup({ closeButton: false, offset: 10, className: 'vx-popup' }))
  const latest = useRef({ snapshot, planRoutes, planStyle, selected })
  const pickRef = useRef({ pick, onPick, onSelect })
  latest.current = { snapshot, planRoutes, planStyle, selected }
  pickRef.current = { pick, onPick, onSelect }

  useEffect(() => {
    const m = new maplibregl.Map({ container: box.current!, style: STYLE, center: CDMX, zoom: 10.6, attributionControl: { compact: true } })
    map.current = m
    m.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'bottom-right')
    m.on('load', () => {
      m.addSource('incidents', { type: 'geojson', data: fc([]) })
      m.addSource('routes', { type: 'geojson', data: fc([]) })
      m.addSource('orders', { type: 'geojson', data: fc([]) })
      m.addLayer({ id: 'incidents-fill', type: 'fill', source: 'incidents', paint: { 'fill-color': '#ff5a5f', 'fill-opacity': ['case', ['get', 'active'], 0.16, 0.04] } })
      m.addLayer({ id: 'incidents-line', type: 'line', source: 'incidents', paint: { 'line-color': '#ff5a5f', 'line-width': 1.5, 'line-dasharray': [2, 2], 'line-opacity': ['case', ['get', 'active'], 0.9, 0.25] } })
      m.addLayer({ id: 'routes-casing', type: 'line', source: 'routes', layout: { 'line-join': 'round', 'line-cap': 'round' }, paint: { 'line-color': '#05080b', 'line-width': ['get', 'casing'], 'line-opacity': ['get', 'opacity'] } })
      m.addLayer({ id: 'routes-line', type: 'line', source: 'routes', layout: { 'line-join': 'round', 'line-cap': 'round' }, paint: { 'line-color': ['get', 'color'], 'line-width': ['get', 'width'], 'line-opacity': ['get', 'opacity'], 'line-dasharray': [1, 0] } })
      m.addLayer({
        id: 'orders-circle', type: 'circle', source: 'orders',
        paint: {
          'circle-radius': ['interpolate', ['linear'], ['zoom'], 9, ['case', ['get', 'urgent'], 4.5, 2.6], 13, ['case', ['get', 'urgent'], 9, 6]],
          'circle-color': ['get', 'color'], 'circle-opacity': ['get', 'opacity'],
          'circle-stroke-color': ['get', 'ring'], 'circle-stroke-width': ['get', 'ringW'],
        },
      })
      ready.current = true
      paint()
    })
    m.on('mouseenter', 'orders-circle', () => { m.getCanvas().style.cursor = 'pointer' })
    m.on('mouseleave', 'orders-circle', () => { m.getCanvas().style.cursor = pickRef.current.pick ? 'crosshair' : ''; popup.current.remove() })
    m.on('mousemove', 'orders-circle', (e) => {
      const f = e.features?.[0]
      if (!f) return
      const p = f.properties as Record<string, string>
      popup.current.setLngLat(e.lngLat).setHTML(p.html).addTo(m)
    })
    m.on('click', (e) => {
      const { pick, onPick, onSelect } = pickRef.current
      if (pick) { onPick(e.lngLat.lat, e.lngLat.lng); return }
      const hit = m.queryRenderedFeatures(e.point, { layers: ['orders-circle'] })
      if (hit.length) { const v = (hit[0].properties as { vehicleId?: number }).vehicleId; onSelect(v ?? null) }
      else onSelect(null)
    })
    return () => { m.remove(); map.current = null; ready.current = false }
  }, [])

  useEffect(() => { if (map.current) map.current.getCanvas().style.cursor = pick ? 'crosshair' : '' }, [pick])

  function paint() {
    const m = map.current
    if (!m || !ready.current) return
    const { snapshot: s, planRoutes: pr, planStyle: ps, selected: sel } = latest.current
    const dim = (vid: number | null) => sel != null && vid !== sel

    // rutas
    const routes: GeoJSON.Feature[] = []
    if (s?.phase === 'live') {
      for (const u of s.units) {
        if (u.path.length < 2) continue
        routes.push({ type: 'Feature', properties: { color: unitColor(u.id), width: sel === u.id ? 4 : 2.4, casing: sel === u.id ? 7 : 4.5, opacity: dim(u.id) ? 0.12 : 0.85 }, geometry: { type: 'LineString', coordinates: u.path } })
      }
    } else if (pr) {
      for (const r of pr) {
        const manual = ps === 'manual'
        routes.push({ type: 'Feature', properties: { color: manual ? '#8b98a8' : unitColor(r.vehicleId), width: manual ? 1.6 : sel === r.vehicleId ? 4 : 2.4, casing: manual ? 3 : 4.5, opacity: dim(r.vehicleId) ? 0.12 : manual ? 0.55 : 0.85 }, geometry: { type: 'LineString', coordinates: r.path } })
      }
    }
    ;(m.getSource('routes') as GeoJSONSource).setData(fc(routes))

    // entregas
    const orders: GeoJSON.Feature[] = (s?.orders ?? []).map((o) => ({
      type: 'Feature',
      properties: {
        color: s?.phase === 'live' ? orderColor(o) : ps === 'manual' ? '#c3ccd6' : o.vehicleId != null ? unitColor(o.vehicleId) : '#8b98a8',
        ring: s?.phase === 'live' ? orderRing(o) : '#0a0f14',
        ringW: o.state === 'riesgo' || o.state === 'atrasado' || o.state === 'sinunidad' ? 2.4 : o.urgent ? 2 : 1,
        opacity: dim(o.vehicleId) ? 0.15 : o.state === 'entregado' ? 0.7 : 1,
        urgent: o.urgent, vehicleId: o.vehicleId ?? undefined, html: orderHtml(o, s?.phase === 'live'),
      },
      geometry: { type: 'Point', coordinates: [o.lon, o.lat] },
    }))
    ;(m.getSource('orders') as GeoJSONSource).setData(fc(orders))

    // incidentes
    const inc: GeoJSON.Feature[] = (s?.incidents ?? []).map((i: IncidentDto) => ({ type: 'Feature', properties: { active: i.active }, geometry: { type: 'Polygon', coordinates: [circle(i.lat, i.lon, i.radiusKm)] } }))
    ;(m.getSource('incidents') as GeoJSONSource).setData(fc(inc))

    // CEDIS y unidades (marcadores HTML: etiquetas nítidas)
    if (s && !depotMarker.current) {
      const el = document.createElement('div')
      el.className = 'vx-depot'
      el.innerHTML = '<span>CEDIS</span>'
      el.title = s.depot.name
      depotMarker.current = new maplibregl.Marker({ element: el }).setLngLat([s.depot.lon, s.depot.lat]).addTo(m)
    }
    const live = s?.phase === 'live' ? s.units : []
    const seen = new Set<number>()
    for (const u of live) {
      if (u.status === 'en CEDIS' && u.pending === 0) continue
      seen.add(u.id)
      let mk = markers.current.get(u.id)
      if (!mk) {
        const el = document.createElement('button')
        el.type = 'button'
        el.className = 'vx-unit'
        el.addEventListener('click', (ev) => { ev.stopPropagation(); pickRef.current.onSelect(latest.current.selected === u.id ? null : u.id) })
        mk = new maplibregl.Marker({ element: el }).setLngLat([u.lon, u.lat]).addTo(m)
        markers.current.set(u.id, mk)
      }
      mk.setLngLat([u.lon, u.lat])
      const el = mk.getElement()
      el.style.setProperty('--c', unitColor(u.id))
      el.dataset.state = u.status === 'descompuesta' ? 'broken' : u.risk
      el.dataset.dim = dim(u.id) ? '1' : '0'
      el.innerHTML = `<i></i><b>${u.code.replace('U-', '')}</b>`
      el.title = `${u.code} · ${u.driver} · ${u.status}`
    }
    for (const [id, mk] of markers.current) if (!seen.has(id)) { mk.remove(); markers.current.delete(id) }
  }

  useEffect(paint, [snapshot, planRoutes, planStyle, selected])

  // MapLibre le pone position: relative a su contenedor: por eso va dentro de otro que ocupa todo
  return <div className="absolute inset-0"><div ref={box} className="h-full w-full" aria-label="Mapa de la operación" /></div>
}

function orderHtml(o: OrderDto, live: boolean) {
  const state: Record<string, string> = { pendiente: 'Pendiente', entregado: 'Entregado', tarde: 'Entregado tarde', riesgo: 'En riesgo', atrasado: 'Llegará tarde', sinunidad: 'Sin unidad' }
  const lines = [
    `<b>${o.code}</b> · ${o.customer}${o.urgent ? ' <em>urgente</em>' : ''}`,
    `<span>${o.area}</span>`,
    `Ventana ${hm(o.open)}–${hm(o.close)} · ${o.kg} kg`,
  ]
  if (live) {
    if (o.deliveredAt != null) lines.push(`${state[o.state]} a las ${hm(o.deliveredAt)}${o.lateMin && o.lateMin > 0.5 ? ` (+${Math.round(o.lateMin)} min)` : ''}`)
    else lines.push(`${state[o.state]}${o.eta != null ? ` · llega ${hm(o.eta)}` : ''}${o.vehicleId != null ? ` · U-${String(o.vehicleId + 1).padStart(2, '0')}` : ''}`)
  }
  return lines.join('<br>')
}

export type { UnitDto }
