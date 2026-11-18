import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // 页面由 Kestrel 以 /app 托管：资源必须带这个前缀，否则 /app/transfers 这类深链接会去找 /assets
  base: '/app/',
  build: {
    outDir: 'dist',
    assetsDir: 'assets',
    // 样张要单文件直开，CSS/JS 内联不行就用相对路径即可
    rollupOptions: {
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: 'assets/[name].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
  server: {
    port: 5199,
    strictPort: true,
  },
});
