import { useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { api, ApiError } from '../../../lib/api'
import type { HostKeyMessage, RemoteSession, SessionPhase } from '../types'
import { sessionOrigin } from '../webSessionOrigin'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface WebSessionProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
  /** The user ended the session; the tab closes. */
  onEnded: () => void
}

interface OpenResponse {
  status: 'ready' | 'trustRequired'
  sessionId: string | null
  launchPath: string | null
  baseDomain: string | null
  target: string | null
  certificate: { fingerprint: string; subject: string; issuer: string; expires: string } | null
}

/**
 * A web (HTTP/HTTPS) session proxied by NKeeper's server and shown inside this tab. The page runs
 * on its own origin in a sandboxed frame: it can't reach NKeeper, navigate NKeeper away, or be
 * framed by anything but NKeeper.
 */
export function WebSession({ session, active, onShowHost, onEnded }: WebSessionProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [url, setUrl] = useState<string | null>(null)
  const [frameKey, setFrameKey] = useState(0)
  const [attempt, setAttempt] = useState(0)
  const sessionIdRef = useRef<string | null>(null)
  const trustRef = useRef<string | undefined>(undefined)

  useEffect(() => {
    let cancelled = false
    const { hostId, serviceIndex } = session.request

    async function open() {
      try {
        const response = await api.post<OpenResponse>('/api/web/sessions', {
          body: { hostId, serviceIndex, trustFingerprint: trustRef.current },
        })
        if (cancelled) {
          if (response.sessionId) {
            void api.delete(`/api/web/sessions/${response.sessionId}`).catch(() => {})
          }
          return
        }
        const target = response.target ?? undefined
        if (response.status === 'trustRequired' && response.certificate) {
          const prompt: HostKeyMessage = {
            type: 'hostKey',
            status: 'unknown',
            kind: 'certificate',
            port: Number(new URL(response.target!).port || 443),
            algorithm: 'TLS certificate',
            fingerprint: response.certificate.fingerprint,
            subject: response.certificate.subject,
            expires: response.certificate.expires,
          }
          setPhase({ kind: 'hostKey', prompt, target })
          return
        }

        const origin = sessionOrigin(response.sessionId!, response.baseDomain, window.location)
        if (!origin) {
          void api.delete(`/api/web/sessions/${response.sessionId}`).catch(() => {})
          setPhase({
            kind: 'ended',
            target,
            message:
              'Web sessions need NKeeper to be opened by a host name, not an IP address (each session gets its own subdomain). ' +
              'Open NKeeper by name, or ask your administrator to set WebProxy:BaseDomain.',
          })
          return
        }

        sessionIdRef.current = response.sessionId
        setUrl(origin + response.launchPath)
        setPhase({ kind: 'connected', target: target ?? origin })
      } catch (error) {
        if (!cancelled) {
          setPhase({
            kind: 'ended',
            code: error instanceof ApiError ? error.code : undefined,
            message: error instanceof ApiError ? error.message : 'Couldn’t start the web session.',
          })
        }
      }
    }

    void open()
    return () => {
      cancelled = true
    }
  }, [session.request, attempt])

  // Closing the tab (or NKeeper) ends the session on the server.
  useEffect(
    () => () => {
      if (sessionIdRef.current) {
        void api.delete(`/api/web/sessions/${sessionIdRef.current}`).catch(() => {})
      }
    },
    [],
  )

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      setPhase({ kind: 'ended', target: phase.target, code: 'host_key_rejected', message: 'The certificate was not trusted, so no session was opened.' })
      return
    }
    trustRef.current = phase.prompt.fingerprint
    setPhase({ kind: 'connecting', target: phase.target })
    setAttempt((n) => n + 1)
  }

  function retry() {
    setUrl(null)
    setPhase({ kind: 'connecting' })
    setAttempt((n) => n + 1)
  }

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} web session`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <>
            <Button variant="ghost" size="sm" onClick={() => setFrameKey((n) => n + 1)}>
              Reload
            </Button>
            <Button variant="ghost" size="sm" onClick={onEnded}>
              End session
            </Button>
          </>
        ) : null}
        {phase.kind === 'ended' ? (
          <Button variant="secondary" size="sm" onClick={retry}>
            Try again
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={`${styles.terminalArea} ${styles.webArea}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={retry} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
        {phase.kind === 'connected' && url ? (
          <iframe
            // Reload re-opens the page; the session cookie is already set, so the launch link redirects home.
            key={frameKey}
            className={styles.webFrame}
            src={url}
            title={`${session.title} — ${phase.target}`}
            // No allow-top-navigation: the page can't navigate NKeeper away.
            sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox allow-downloads allow-modals"
            allow="clipboard-read; clipboard-write; fullscreen"
            referrerPolicy="no-referrer"
          />
        ) : null}
      </div>
    </div>
  )
}
