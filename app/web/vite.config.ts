import { defineConfig } from 'vite';
import path from 'node:path';
const CORE_WEB = '../core/NexysView.Web/src';   // nexys-core shared page code (bridge, plot, icons, util, types)
export default defineConfig({
  base: './',
  resolve: { alias: { '@core': path.resolve(__dirname, CORE_WEB) } },
  build: { outDir: 'dist', emptyOutDir: true, target: 'es2022', sourcemap: false, assetsInlineLimit: 0, rollupOptions: { output: { entryFileNames: 'app.js', assetFileNames: 'assets/[name][extname]', chunkFileNames: 'chunk-[name].js' } } },
  server: { port: 5173 },
});
