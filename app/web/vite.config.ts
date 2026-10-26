import { defineConfig } from 'vite';
export default defineConfig({
  base: './',
  build: { outDir: 'dist', emptyOutDir: true, target: 'es2022', sourcemap: false, assetsInlineLimit: 0, rollupOptions: { output: { entryFileNames: 'app.js', assetFileNames: 'assets/[name][extname]', chunkFileNames: 'chunk-[name].js' } } },
  server: { port: 5173 },
});
