import { fileURLToPath, URL } from 'node:url';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const shim = (file: string) => fileURLToPath(new URL(`./src/shims/${file}`, import.meta.url));

// Built into ../wwwroot, which the NinjaQueue.Dashboard assembly embeds and serves at MapNinjaQueueDashboard's path.
// Relative asset URLs ('./') let the same build run under any path; routes live in the URL hash.
export default defineConfig({
  base: './',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@shared': shim('shared.ts'),
      '@ninja-icon': shim('ninja-icon.tsx'),
      'next/navigation': shim('next-navigation.ts'),
    },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
  },
  server: {
    // npm run dev: proxy the API to a running host (dotnet run the demo) at /jobs.
    proxy: { '/jobs/api': 'http://localhost:8080' },
  },
});