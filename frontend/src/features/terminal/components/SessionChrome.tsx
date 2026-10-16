import type { ReactNode } from 'react'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import type { HostKeyMessage, SessionPhase } from '../types'
import styles from './Session.module.css'

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

/** The thin bar above a session: state, target and actions. */
export function SessionStatusBar({ phase, children }: { phase: SessionPhase; children?: ReactNode }) {
  return (
    <div className={styles.statusBar} role="status">
      <span className={`${styles.dot} ${styles[phase.kind]} ${phase.kind === 'ended' ? styles.failed : ''}`} />
      <span className={styles.statusText}>
        {phase.kind === 'connected'
          ? 'Connected'
          : phase.kind === 'ended'
            ? 'Not connected'
            : phase.kind === 'hostKey'
              ? phase.prompt.kind === 'certificate'
                ? 'Waiting for certificate confirmation'
                : 'Waiting for host key confirmation'
              : 'Connecting…'}
      </span>
      {phase.target ? <span className={styles.target}>{phase.target}</span> : null}
      <div className={styles.statusActions}>{children}</div>
    </div>
  )
}

/** Shown before the server has answered at all. */
export function OpeningCard() {
  return (
    <div className={styles.overlay}>
      <div className={styles.card}>
        <Spinner /> Opening session…
      </div>
    </div>
  )
}

/** Trust on first use: the user confirms the server's identity before credentials are used. */
export function TrustCard({ prompt, target, onDecide }: { prompt: HostKeyMessage; target?: string; onDecide: (trust: boolean) => void }) {
  if (prompt.status === 'changed') {
    // The server refuses the connection; an error message follows.
    return null
  }
  const certificate = prompt.kind === 'certificate'
  return (
    <div className={styles.overlay}>
      <div className={styles.card} role="alertdialog" aria-labelledby="trust-title" aria-describedby="trust-text">
        <h3 id="trust-title" className={styles.cardTitle}>
          {certificate ? 'Trust this certificate?' : 'Trust this host key?'}
        </h3>
        <p id="trust-text">
          This is the first connection to {target ?? `port ${prompt.port}`}. Check that the fingerprint matches the server’s before
          trusting it; NKeeper will refuse the connection if it ever changes.
          {certificate ? ' Self-signed certificates are normal for Remote Desktop.' : null}
        </p>
        <dl className={styles.fingerprint}>
          <dt>{certificate ? 'SHA-256 fingerprint' : prompt.algorithm}</dt>
          <dd>{certificate ? prompt.fingerprint : `SHA256:${prompt.fingerprint}`}</dd>
          {prompt.subject ? (
            <>
              <dt>Issued to</dt>
              <dd>{prompt.subject}</dd>
            </>
          ) : null}
          {prompt.expires ? (
            <>
              <dt>Expires</dt>
              <dd>{dateFormat.format(new Date(prompt.expires))}</dd>
            </>
          ) : null}
        </dl>
        {certificate ? (
          <p className={styles.hint}>On Windows, compare with the “Remote Desktop” certificate in the computer’s certificate store (certlm.msc).</p>
        ) : null}
        <div className={styles.cardActions}>
          <Button variant="secondary" size="sm" onClick={() => onDecide(false)}>
            Cancel
          </Button>
          <Button size="sm" onClick={() => onDecide(true)} autoFocus>
            Trust and connect
          </Button>
        </div>
      </div>
    </div>
  )
}

interface EndedCardProps {
  message: string
  code?: string
  canReconnect: boolean
  onReconnect: () => void
  onShowHost: () => void
}

/** Why a session failed, with the way forward. */
export function EndedCard({ message, code, canReconnect, onReconnect, onShowHost }: EndedCardProps) {
  const identityChanged = code === 'host_key_changed'
  return (
    <div className={styles.overlay}>
      <div className={`${styles.card} ${styles.errorCard}`} role="alert">
        <h3 className={styles.cardTitle}>{identityChanged ? 'Server identity changed' : 'Couldn’t connect'}</h3>
        <p>{message}</p>
        <div className={styles.cardActions}>
          {identityChanged || code === 'security_negotiation_failed' ? (
            <Button variant="secondary" size="sm" onClick={onShowHost}>
              Show host details
            </Button>
          ) : null}
          {canReconnect ? (
            <Button size="sm" onClick={onReconnect}>
              Try again
            </Button>
          ) : (
            <p className={styles.hint}>Open a new session from the host’s menu to sign in again.</p>
          )}
        </div>
      </div>
    </div>
  )
}
