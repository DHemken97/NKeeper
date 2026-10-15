import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')

  return {
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      // In development the browser talks to Vite only; /api is forwarded to the ASP.NET Core API,
      // so cookies stay same-origin exactly as they do behind nginx in Docker.
      proxy: {
        '/api': {
          target: env.DEV_API_PROXY_TARGET || 'http://localhost:5080',
          changeOrigin: false,
        },
      },
    },
    test: {
      environment: 'jsdom',
      setupFiles: ['./src/test/setup.ts'],
      restoreMocks: true,
      unstubGlobals: true,
      css: { modules: { classNameStrategy: 'non-scoped' } },
    },
  }
})
