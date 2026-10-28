import react from '@vitejs/plugin-react'
import { basename, resolve } from 'node:path'
import { defineConfig } from 'vite'

/*
 * Builds one plugin's browser part into a single script (and stylesheet) for its package:
 *
 *   NKEEPER_PLUGIN=../plugins/zabbix/frontend NKEEPER_PLUGIN_OUT=<dir> npx vite build -c vite.plugin.config.ts
 *
 * React, React Router and the SDK are left out: at run time the script uses the web app's own,
 * which it finds on window.__NKEEPER__ (see src/sdk/runtime.ts).
 */
const plugin = process.env.NKEEPER_PLUGIN
const out = process.env.NKEEPER_PLUGIN_OUT
if (!plugin || !out) {
  throw new Error('Set NKEEPER_PLUGIN (the plugin\'s frontend directory) and NKEEPER_PLUGIN_OUT (where to build it).')
}

const runtime: Record<string, string> = {
  react: '__NKEEPER__.React',
  'react/jsx-runtime': '__NKEEPER__.jsxRuntime',
  'react-dom': '__NKEEPER__.ReactDOM',
  'react-router': '__NKEEPER__.ReactRouter',
  '@nkeeper/plugin-sdk': '__NKEEPER__.sdk',
}

export default defineConfig({
  plugins: [react()],
  define: { 'process.env.NODE_ENV': JSON.stringify('production') },
  build: {
    outDir: resolve(out),
    emptyOutDir: true,
    copyPublicDir: false,
    sourcemap: false,
    lib: {
      entry: resolve(plugin, 'src/index.tsx'),
      formats: ['iife'],
      // A global of its own, so plugins' scripts don't overwrite each other's.
      name: `NKeeperPlugin_${basename(resolve(plugin, '..')).replace(/[^A-Za-z0-9]/g, '_')}`,
      fileName: () => 'index.js',
      cssFileName: 'index',
    },
    rollupOptions: {
      external: Object.keys(runtime),
      output: { globals: runtime },
    },
  },
})
