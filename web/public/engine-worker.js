// Corre el motor de Vértice (.NET compilado a WebAssembly) fuera del hilo de la interfaz:
// optimizar 150 entregas no congela el mapa. Va como JS simple (sin empaquetar) para que
// el runtime de .NET cargue sus archivos de _framework tal cual.
//
// El runtime se arranca al evaluar el módulo (no dentro del manejador de mensajes): así lo
// espera dotnet.js cuando corre en un worker.
const started = performance.now()
const ready = (async () => {
  const { dotnet } = await import('./_framework/dotnet.js')
  const runtime = await dotnet.create()
  runtime.setModuleImports('vertice', {
    // el optimizador reporta su avance para la gráfica en vivo
    progress: (it, ms, best) => postMessage({ type: 'progress', it, ms, best }),
  })
  const config = runtime.getConfig()
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName)
  await runtime.runMain(config.mainAssemblyName, [])
  return { api: exports.Interop, ms: Math.round(performance.now() - started) }
})()

self.addEventListener('message', async (e) => {
  const { id, op, args = [] } = e.data ?? {}
  if (id === undefined) return
  try {
    const { api, ms } = await ready
    let result
    if (op === 'boot') result = ms
    else {
      const fn = api[op]
      if (!fn) throw new Error('Operación desconocida: ' + op)
      result = fn(...args)
    }
    postMessage({ id, ok: true, result })
  } catch (err) {
    postMessage({ id, ok: false, error: err instanceof Error ? err.message : String(err) })
  }
})
