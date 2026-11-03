import { useEffect } from 'react'
import { deleteOnPageExit } from './api'

/** Paths to DELETE when the page closes, with how many open things registered each. */
const paths = new Map<string, number>()
let listening = false

function closeAll() {
  for (const path of paths.keys()) {
    deleteOnPageExit(path)
  }
}

/**
 * Closes a session kept on the server (a SQL Server, web or VM console session) when the browser tab
 * or window closes, not only when its NKeeper tab does: `path` is DELETEd as the page goes. Call the
 * returned function once the session is closed some other way. Sessions over a WebSocket (SSH, RDP…)
 * need none of this: they end when the page's sockets close.
 */
export function closeOnPageExit(path: string): () => void {
  if (!listening) {
    window.addEventListener('pagehide', closeAll)
    listening = true
  }
  paths.set(path, (paths.get(path) ?? 0) + 1)
  let done = false
  return () => {
    if (done) {
      return
    }
    done = true
    const count = (paths.get(path) ?? 1) - 1
    if (count > 0) {
      paths.set(path, count)
    } else {
      paths.delete(path)
    }
  }
}

/** {@link closeOnPageExit} for as long as the component is mounted with that path. */
export function useCloseOnPageExit(path: string | null) {
  useEffect(() => (path ? closeOnPageExit(path) : undefined), [path])
}

/** The paths that would be closed now (for tests). */
export function pendingPageExitPaths(): string[] {
  return [...paths.keys()]
}
