// Publica el motor .NET a WebAssembly y copia _framework a web/public (la interfaz lo carga desde ahí)
import { execSync } from 'node:child_process'
import { cpSync, rmSync, existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const proj = path.join(root, 'src/Vertice.Wasm/Vertice.Wasm.csproj')
const out = path.join(root, 'src/Vertice.Wasm/bin/publish')
if (!process.argv.includes('--skip-build')) rmSync(out, { recursive: true, force: true })   // sin restos de publicaciones anteriores
if (!process.argv.includes('--skip-build')) execSync(`dotnet publish "${proj}" -c Release -o "${out}"`, { stdio: 'inherit' })
const fw = path.join(out, 'wwwroot/_framework')
if (!existsSync(fw)) throw new Error('No se encontró ' + fw)
const dest = path.join(root, 'web/public/_framework')
rmSync(dest, { recursive: true, force: true })
cpSync(fw, dest, { recursive: true })
console.log('motor copiado a', dest)
