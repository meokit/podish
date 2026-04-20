import {defineConfig} from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'
import fs from 'fs'

const repoRoot = path.resolve(__dirname, '../..')

export default defineConfig({
    plugins: [
        react(),
        tailwindcss(),
        {
            name: 'copy-third-party-notices',
            closeBundle() {
                const src = path.join(repoRoot, 'THIRD_PARTY_NOTICES.md')
                const dest = path.join(__dirname, 'dist', 'THIRD_PARTY_NOTICES.md')
                if (fs.existsSync(src)) {
                    fs.copyFileSync(src, dest)
                }
            },
        },
    ],
    build: {
        outDir: '../wwwroot',
        emptyOutDir: false,
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
