import { useSyncExternalStore } from 'react'
import type { NKeeperPlugin } from './types'

let plugins: readonly NKeeperPlugin[] = []
const listeners = new Set<() => void>()

/**
 * Registers a plugin's browser part. A plugin's entry script calls it once; a second registration
 * of the same ID is ignored. The web app shows what it adds from then on.
 */
export function definePlugin(plugin: NKeeperPlugin): NKeeperPlugin {
  if (!plugins.some((p) => p.id === plugin.id)) {
    plugins = [...plugins, plugin]
    for (const listener of listeners) {
      listener()
    }
  }
  return plugin
}

/** The registered plugins, in the order they registered. */
export function registeredPlugins(): readonly NKeeperPlugin[] {
  return plugins
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** The registered plugins, re-rendering when another registers. */
export function usePlugins(): readonly NKeeperPlugin[] {
  return useSyncExternalStore(subscribe, registeredPlugins)
}

/** Forgets every plugin (tests). */
export function resetPlugins(): void {
  plugins = []
  for (const listener of listeners) {
    listener()
  }
}

let loading = false
const loadingListeners = new Set<() => void>()

/** Marks the server's plugins as being loaded (or done). */
export function setPluginsLoading(value: boolean): void {
  loading = value
  for (const listener of loadingListeners) {
    listener()
  }
}

/** Whether the server's plugins are still being loaded, so a plugin's page shouldn't be called missing yet. */
export function usePluginsLoading(): boolean {
  return useSyncExternalStore(
    (listener) => {
      loadingListeners.add(listener)
      return () => {
        loadingListeners.delete(listener)
      }
    },
    () => loading,
  )
}
