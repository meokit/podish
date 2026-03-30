import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
  ],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: false,  // Don't delete _framework/ from dotnet publish
  },
  server: {
    headers: {
      'Cross-Origin-Embedder-Policy': 'require-corp',
      'Cross-Origin-Opener-Policy': 'same-origin',
    },
    proxy: {
      '/_framework': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
})
