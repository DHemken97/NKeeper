import { useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Button } from '../../../components/ui/Button'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../api/inventoryApi'
import { formatAccount, formatTag, PROTOCOL_INFO, type Host, type HostTag } from '../types'
import { DeleteButton } from './DeleteButton'
import styles from './HostPanel.module.css'

interface HostDetailsProps {
  host: Host
  onEdit: () => void
  onDeleted: (id: string) => void
  /** Filter the tree to hosts carrying this tag. */
  onTagClick: (tag: HostTag) => void
}

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** Read-only overview of a host: addresses, tags, services and credentials (never secrets). */
export function HostDetails({ host, onEdit, onDeleted, onTagClick }: HostDetailsProps) {
  const [deleting, setDeleting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleDelete() {
    setDeleting(true)
    setError(null)
    try {
      await hostsApi.remove(host.id)
      onDeleted(host.id)
    } catch (err) {
      // Already gone is what the user wanted.
      if (err instanceof ApiError && err.status === 404) {
        onDeleted(host.id)
        return
      }
      setError(toFormErrors(err).message)
      setDeleting(false)
    }
  }

  const addresses = [host.hostname, host.ipAddress].filter(Boolean).join(' · ')

  return (
    <article className={styles.panel} aria-labelledby="host-title">
      <header className={styles.header}>
        <div className={styles.heading}>
          <h1 id="host-title" className={styles.title}>
            {host.name}
          </h1>
          <p className={`${styles.subtitle} ${styles.mono}`}>{addresses}</p>
        </div>
        <div className={styles.actions}>
          <DeleteButton question="Delete this host?" onConfirm={handleDelete} loading={deleting} />
          <Button onClick={onEdit} disabled={deleting}>
            Edit
          </Button>
        </div>
      </header>

      <div className={styles.body}>
        {error ? <Alert>{error}</Alert> : null}

        <section className={styles.section} aria-labelledby="overview-title">
          <h2 id="overview-title" className={styles.sectionTitle}>
            Overview
          </h2>
          <dl className={styles.properties}>
            <dt>Hostname</dt>
            <dd className={styles.mono}>{host.hostname ?? <span className={styles.muted}>—</span>}</dd>
            <dt>IP address</dt>
            <dd className={styles.mono}>{host.ipAddress ?? <span className={styles.muted}>—</span>}</dd>
            <dt>MAC address</dt>
            <dd className={styles.mono}>{host.macAddress ?? <span className={styles.muted}>—</span>}</dd>
            <dt>Updated</dt>
            <dd>
              <time dateTime={host.updatedAtUtc}>{dateTimeFormat.format(new Date(host.updatedAtUtc))}</time>
            </dd>
            {host.description ? (
              <>
                <dt>Description</dt>
                <dd className={styles.description}>{host.description}</dd>
              </>
            ) : null}
          </dl>
        </section>

        <section className={styles.section} aria-labelledby="tags-title">
          <h2 id="tags-title" className={styles.sectionTitle}>
            Tags
          </h2>
          {host.tags.length === 0 ? (
            <p className={styles.muted}>No tags. Add tags like site, env or role to group hosts in the tree.</p>
          ) : (
            <ul className={styles.chips}>
              {host.tags.map((tag) => (
                <li key={`${tag.name}=${tag.value ?? ''}`}>
                  <button
                    type="button"
                    className={styles.chip}
                    onClick={() => onTagClick(tag)}
                    title="Show hosts with this tag"
                  >
                    <span className={styles.chipName}>{tag.name}</span>
                    {tag.value ? <span className={styles.chipValue}>{tag.value}</span> : null}
                    <span className="visually-hidden">: show hosts with {formatTag(tag)}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className={styles.section} aria-labelledby="services-title">
          <h2 id="services-title" className={styles.sectionTitle}>
            Services
          </h2>
          {host.services.length === 0 ? (
            <p className={styles.muted}>No services. Edit the host to add SSH, RDP, VNC and more.</p>
          ) : (
            <ul className={styles.rows}>
              {host.services.map((service, index) => {
                const info = PROTOCOL_INFO[service.protocol]
                return (
                  <li key={index} className={styles.row}>
                    <Badge variant={index === 0 ? 'accent' : 'neutral'}>{info.label}</Badge>
                    <span className={styles.rowMain}>{service.label ?? info.name}</span>
                    <span className={`${styles.mono} ${styles.muted}`}>
                      {service.port ? `port ${service.port}` : info.defaultPort ? `port ${info.defaultPort} (default)` : ''}
                    </span>
                  </li>
                )
              })}
            </ul>
          )}
        </section>

        <section className={styles.section} aria-labelledby="credentials-title">
          <h2 id="credentials-title" className={styles.sectionTitle}>
            Credentials
          </h2>
          {host.credentials.length === 0 ? (
            <p className={styles.muted}>No credentials saved for this host.</p>
          ) : (
            <ul className={styles.rows}>
              {host.credentials.map((credential) => {
                const account = formatAccount(credential)
                return (
                  <li key={credential.id} className={styles.row}>
                    <div className={styles.rowMain}>
                      <div className={styles.mono}>{account ?? credential.label}</div>
                      {account && credential.label ? <div className={styles.muted}>{credential.label}</div> : null}
                      {credential.notes ? <div className={styles.muted}>{credential.notes}</div> : null}
                    </div>
                    <div className={styles.badges}>
                      {credential.hasPassword ? <Badge>Password</Badge> : null}
                      {credential.hasPrivateKey ? <Badge>SSH key</Badge> : null}
                      {!credential.hasPassword && !credential.hasPrivateKey ? <span className={styles.muted}>No secret</span> : null}
                    </div>
                  </li>
                )
              })}
            </ul>
          )}
        </section>
      </div>
    </article>
  )
}
