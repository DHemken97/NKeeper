import { useId } from 'react'
import { formatAccount, type Host } from '../types'
import listStyles from './ListEditor.module.css'
import styles from './HostPanel.module.css'

interface JumpHostFieldProps {
  /** Hosts this one could be reached through: SSH hosts NKeeper reaches directly (not this one). */
  hosts: Host[]
  /** The jump host's id, or empty for none (connect directly). */
  value: string
  credentialId: string
  onChange: (jumpHostId: string, credentialId: string) => void
  error?: string
  credentialError?: string
}

/**
 * "Reach through": a bastion (jump host) NKeeper tunnels this host's connections through over SSH,
 * and which of its credentials to sign in to it with.
 */
export function JumpHostField({ hosts, value, credentialId, onChange, error, credentialError }: JumpHostFieldProps) {
  const id = useId()
  const jump = hosts.find((h) => h.id === value)
  const usable = jump?.credentials.filter((c) => c.username && (c.hasPassword || c.hasPrivateKey)) ?? []

  return (
    <div className={`${styles.full} ${styles.jump}`}>
      <div className={styles.field}>
        <label htmlFor={id} className={styles.fieldLabel}>
          Reach through
        </label>
        <select
          id={id}
          className={listStyles.input}
          value={value}
          onChange={(event) => onChange(event.target.value, '')}
          aria-invalid={error ? true : undefined}
          aria-describedby={`${id}-hint`}
        >
          <option value="">Directly (no jump host)</option>
          {hosts.map((h) => (
            <option key={h.id} value={h.id}>
              {h.name}
            </option>
          ))}
        </select>
        {error ? <p className={styles.fieldError}>{error}</p> : null}
        <p id={`${id}-hint`} className={styles.fieldHint}>
          SSH, Telnet, files, web, iDRAC and KVM connections are tunnelled through this host over SSH. RDP and VNC can’t go through a jump host yet.
        </p>
      </div>
      {jump ? (
        <div className={styles.field}>
          <label htmlFor={`${id}-credential`} className={styles.fieldLabel}>
            Sign in to {jump.name} as
          </label>
          <select
            id={`${id}-credential`}
            className={listStyles.input}
            value={credentialId}
            onChange={(event) => onChange(value, event.target.value)}
            aria-invalid={credentialError ? true : undefined}
          >
            <option value="">Its first usable credential</option>
            {usable.map((c) => (
              <option key={c.id} value={c.id}>
                {c.label ?? formatAccount(c)}
              </option>
            ))}
          </select>
          {credentialError ? <p className={styles.fieldError}>{credentialError}</p> : null}
        </div>
      ) : null}
    </div>
  )
}
