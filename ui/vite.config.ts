import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Built into ../src/GuardianX.App/wwwroot and served by WebView2 from a virtual host, so paths stay relative.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: { outDir: '../src/GuardianX.App/wwwroot', emptyOutDir: true, target: 'chrome120', chunkSizeWarningLimit: 900 },
})
