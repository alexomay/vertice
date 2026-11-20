import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// VERTICE_BASE=/vertice/ al publicar en alexmar.mx/vertice; la API la sirve en la raíz
export default defineConfig({
  base: process.env.VERTICE_BASE || '/',
  plugins: [react(), tailwindcss()],
  worker: { format: 'es' },
  server: {
    port: 5180,
    proxy: { '/hub': { target: 'http://localhost:5090', ws: true }, '/api': 'http://localhost:5090' },
  },
  build: { outDir: process.env.VERTICE_OUT || 'dist', emptyOutDir: true, chunkSizeWarningLimit: 1500 },
})
