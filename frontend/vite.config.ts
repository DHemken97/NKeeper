import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig, loadEnv } from 'vite'

const source = (path: string) => fileURLToPath(new URL(path, import.meta.url))

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')

  return {
    plugins: [react()],
    resolve: {
      // NKeeper's own plugins (../plugins/*/frontend) are tested and type-checked against the web
      // app's SDK and React; their builds leave both out (see vite.plugin.config.ts).
      alias: [
        { find: /^@nkeeper\/plugin-sdk\/testing$/, replacement: source('./src/sdk/testing.ts') },
        { find: /^@nkeeper\/plugin-sdk$/, replacement: source('./src/sdk/index.ts') },
      ],
      dedupe: ['react', 'react-dom', 'react-router', '@testing-library/react', '@testing-library/user-event'],
    },
    server: {
      port: 5173,
      strictPort: true,
      // NKeeper's own plugins are beside the web app, under ../plugins.
      fs: { allow: [source('..')] },
      // In development the browser talks to Vite only; /api is forwarded to the ASP.NET Core API,
      // so cookies stay same-origin exactly as they do behind nginx in Docker.
      proxy: {
        '/api': {
          target: env.DEV_API_PROXY_TARGET || 'http://localhost:5080',
          changeOrigin: false,
          // Terminal sessions (/api/ssh/session) are WebSockets.
          ws: true,
        },
      },
    },
    test: {
      environment: 'jsdom',
      include: ['src/**/*.test.{ts,tsx}', '../plugins/*/frontend/src/**/*.test.{ts,tsx}'],
      setupFiles: ['./src/test/setup.ts'],
      restoreMocks: true,
      unstubGlobals: true,
      css: { modules: { classNameStrategy: 'non-scoped' } },
    },
  }
})
