import { api } from '../lib/api'
import { setPluginsLoading } from './registry'

/** A loaded plugin's browser part (GET /api/plugins): its signed script and styles on this server. */
export interface PluginUi {
  id: string
  name: string
  version: string
  script: string | null
  styles: string[]
}

const loaded = new Set<string>()

/**
 * Loads the browser parts of the plugins the server loaded: their styles, then their scripts, which
 * register themselves with definePlugin. Each is loaded once per page; one that fails to load is
 * reported and skipped.
 */
export async function loadServerPlugins(signal?: AbortSignal): Promise<void> {
  setPluginsLoading(true)
  try {
    await loadAll(signal)
  } finally {
    setPluginsLoading(false)
  }
}

async function loadAll(signal?: AbortSignal): Promise<void> {
  const plugins = await api.get<PluginUi[]>('/api/plugins', { signal })
  await Promise.all(
    plugins
      .filter((plugin) => !loaded.has(plugin.id))
      .map(async (plugin) => {
        loaded.add(plugin.id)
        for (const href of plugin.styles) {
          const link = document.createElement('link')
          link.rel = 'stylesheet'
          link.href = href
          link.dataset.plugin = plugin.id
          document.head.append(link)
        }
        if (plugin.script) {
          try {
            await loadScript(plugin.script, plugin.id)
          } catch {
            console.error(`The ${plugin.name} plugin (${plugin.version}) couldn’t be loaded.`)
          }
        }
      }),
  )
}

function loadScript(src: string, pluginId: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const script = document.createElement('script')
    script.src = src
    script.async = true
    script.dataset.plugin = pluginId
    script.onload = () => resolve()
    script.onerror = () => reject(new Error(`Couldn't load ${src}`))
    document.head.append(script)
  })
}
