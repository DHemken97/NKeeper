import { useCallback, useEffect, useRef, useState } from 'react'

export interface Resource<T> {
  data: T | undefined
  error: unknown
  loading: boolean
  /** Fetch again, keeping the current data visible while loading. */
  reload: () => void
}

interface Settled<T> {
  key: string
  data: T | undefined
  error: unknown
}

/**
 * Loads data with `load`, re-running whenever `key` changes (e.g. a serialized query).
 * In-flight requests are aborted when superseded or on unmount, so stale responses never
 * overwrite newer ones. Previous data stays available while the next request is loading.
 */
export function useResource<T>(load: (signal: AbortSignal) => Promise<T>, key = ''): Resource<T> {
  const loadRef = useRef(load)
  const [nonce, setNonce] = useState(0)
  const [settled, setSettled] = useState<Settled<T>>({ key: '', data: undefined, error: null })
  const requestKey = `${key}#${nonce}`

  useEffect(() => {
    loadRef.current = load
  })

  useEffect(() => {
    const controller = new AbortController()
    loadRef.current(controller.signal).then(
      (data) => setSettled({ key: requestKey, data, error: null }),
      (error: unknown) => {
        if (!controller.signal.aborted) {
          setSettled((previous) => ({ key: requestKey, data: previous.data, error }))
        }
      },
    )
    return () => controller.abort()
  }, [requestKey])

  const reload = useCallback(() => setNonce((n) => n + 1), [])
  return { data: settled.data, error: settled.error, loading: settled.key !== requestKey, reload }
}
