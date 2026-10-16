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
 * A web (HTTP/HTTPS) session. NKeeper's server connects to the host's web interface and relays
 * it; the site opens in a new browser tab on an isolated origin. This tab shows the session's
 * state and ends the session when closed.
 */
export function WebSession({ session, active, onShowHost }: WebSessionProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [url, setUrl] = useState<string | null>(null)
  const [blocked, setBlocked] = useState(false)
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
        const launch = origin + response.launchPath
        setUrl(launch)
        setPhase({ kind: 'connected', target: target ?? origin })
        // Usually allowed: the click that started this is recent. Otherwise the button below does it.
        const opened = window.open(launch, '_blank', 'noopener')
        setBlocked(opened === null && !document.hasFocus())
      } catch (error) {
        if (cancelled) {
          return
        }
        const code = error instanceof ApiError ? error.code : undefined
        setPhase({
          kind: 'ended',
          code,
          message: error instanceof ApiError ? error.message : 'Couldn’t start the web session.',
        })
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

  function restart() {
    if (sessionIdRef.current) {
      void api.delete(`/api/web/sessions/${sessionIdRef.current}`).catch(() => {})
      sessionIdRef.current = null
    }
    setUrl(null)
    setPhase({ kind: 'connecting' })
    setAttempt((n) => n + 1)
  }

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} web session`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'ended' ? (
          <Button variant="secondary" size="sm" onClick={restart}>
            Try again
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={`${styles.terminalArea} ${styles.webArea}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={restart} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
        {phase.kind === 'connected' && url ? (
          <div className={styles.overlay}>
            <div className={styles.card}>
              <h3 className={styles.cardTitle}>{blocked ? 'Ready to open' : 'Opened in a new browser tab'}</h3>
              <p>
                NKeeper connects to <span className={styles.mono}>{phase.target}</span> for you, so this works even when your computer can’t reach the host.
                The session stays open while this tab is open and ends after 30 minutes without activity.
              </p>
              <div className={styles.cardActions}>
                <Button variant="secondary" size="sm" onClick={restart}>
                  End session
                </Button>
                <a className={styles.linkButton} href={url} target="_blank" rel="noopener noreferrer">
                  {blocked ? `Open ${session.title}` : 'Open again'}
                </a>
              </div>
            </div>
          </div>
        ) : null}
      </div>
    </div>
  )
}
