import { useEffect, useRef } from 'react'
import { ApiError } from '../../lib/api'
import { sqlServerApi } from './sqlServerApi'

/** How often an open tab tells the server its SQL Server session is still in use (it closes idle ones after 15 minutes). */
export const KEEP_ALIVE_MS = 5 * 60 * 1000

/** Keeps a server-side SQL Server session open while the component is mounted; `onEnded` when the server has closed it. */
export function useKeepAlive(sessionId: string | null, onEnded: () => void) {
  const onEndedRef = useRef(onEnded)
  useEffect(() => {
    onEndedRef.current = onEnded
  })

  useEffect(() => {
    if (!sessionId) {
      return
    }
    const timer = window.setInterval(() => {
      sqlServerApi.keepAlive(sessionId).catch((error: unknown) => {
        if (error instanceof ApiError && error.code === 'session_not_found') {
          onEndedRef.current()
        }
      })
    }, KEEP_ALIVE_MS)
    return () => window.clearInterval(timer)
  }, [sessionId])
}
