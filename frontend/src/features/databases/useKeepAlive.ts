import { useEffect, useRef } from 'react'
import { ApiError } from '../../lib/api'
import { useDbApi } from './engineContext'
import type { DbApi } from './databaseApi'

/** How often an open tab tells the server its database session is still in use (it closes ones not heard from in 3 minutes). */
export const KEEP_ALIVE_MS = 60 * 1000

/**
 * Keeps a server-side database session open while the component is mounted; `onEnded` when the server has closed it.
 * The engine's API comes from the tab's context, unless `api` says (the tab itself renders that context, so it must).
 */
export function useKeepAlive(sessionId: string | null, onEnded: (endedId: string) => void, api?: DbApi) {
  const fromContext = useDbApi()
  const db = api ?? fromContext
  const onEndedRef = useRef(onEnded)
  useEffect(() => {
    onEndedRef.current = onEnded
  })

  useEffect(() => {
    if (!sessionId) {
      return
    }
    const timer = window.setInterval(() => {
      db.keepAlive(sessionId).catch((error: unknown) => {
        if (error instanceof ApiError && error.code === 'session_not_found') {
          // Says which session ended: by the time this answer comes, the caller may have opened another.
          onEndedRef.current(sessionId)
        }
      })
    }, KEEP_ALIVE_MS)
    return () => window.clearInterval(timer)
  }, [db, sessionId])
}
