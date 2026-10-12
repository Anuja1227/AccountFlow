import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The dev server proxies /api to the ASP.NET Core API so the browser talks to a
// single origin. That lets the HttpOnly, SameSite=Strict auth cookie work
// without exposing tokens to JavaScript.
const apiTarget = process.env.ACCOUNTFLOW_API ?? 'https://localhost:7242'

export default defineConfig({
    plugins: [react()],
    server: {
        port: 5173,
        strictPort: true,
        proxy: {
            '/api': {
                target: apiTarget,
                changeOrigin: true,
                secure: false, // local ASP.NET Core development certificate
            },
        },
        watch: {
            ignored: ['**/.vs/**', '**/Frontend.slnx/**'],
        },
    },
})
