import { useEffect, useRef } from 'react'
import { ApiError } from '../../lib/api'
import { useDbApi } from './engineContext'

/** How often an open tab tells the server its database session is still in use (it closes ones not heard from in 3 minutes). */
export const KEEP_ALIVE_MS = 60 * 1000

/** Keeps a server-side database session open while the component is mounted; `onEnded` when the server has closed it. */
export function useKeepAlive(sessionId: string | null, onEnded: () => void) {
  const db = useDbApi()
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
          onEndedRef.current()
        }
      })
    }, KEEP_ALIVE_MS)
    return () => window.clearInterval(timer)
  }, [db, sessionId])
}
