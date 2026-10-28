import * as React from 'react'
import * as ReactDOM from 'react-dom'
import * as jsxRuntime from 'react/jsx-runtime'
import * as ReactRouter from 'react-router'
import * as sdk from './index'

/** What plugin bundles find at run time in place of the modules their build leaves out (see vite.plugin.config.ts). */
export interface NKeeperRuntime {
  React: typeof React
  ReactDOM: typeof ReactDOM
  jsxRuntime: typeof jsxRuntime
  ReactRouter: typeof ReactRouter
  sdk: typeof sdk
}

declare global {
  interface Window {
    __NKEEPER__?: NKeeperRuntime
  }
}

/** Makes the web app's React, React Router and SDK available to plugin scripts, before any loads. */
export function exposeRuntime(): void {
  window.__NKEEPER__ = { React, ReactDOM, jsxRuntime, ReactRouter, sdk }
}
