import { useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Button } from '../../../components/ui/Button'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { healthApi, hostsApi } from '../api/inventoryApi'
import {
  CERTIFICATE_WARNING_DAYS,
  checkFor,
  daysUntil,
  formatAccount,
  formatMemory,
  formatTag,
  isSupportedProtocol,
  PROTOCOL_INFO,
  RDP_SECURITY_INFO,
  type Host,
  type HostTag,
  type ServiceCheck,
  type TrustedKey,
} from '../types'
import { DeleteButton } from './DeleteButton'
import styles from './HostPanel.module.css'

interface HostDetailsProps {
  host: Host
  onEdit: () => void
  onDeleted: (id: string) => void
  /** Filter the tree to hosts carrying this tag. */
  onTagClick: (tag: HostTag) => void
  /** Open the Connect menu below the given button. */
  onConnectMenu: (anchor: DOMRect) => void
  /** The host changed here (e.g. host keys forgotten). */
  onChanged: (host: Host) => void
  /** Every host, to show the one this VM runs on and the VMs running on this one. */
  allHosts: Host[]
  /** Show another host (e.g. the hypervisor, or a VM on this one). */
  onShowHost: (id: string) => void
  /** The latest checks of the host's services. */
  checks?: ServiceCheck[]
  /** Fetch the latest checks again (after checking now). */
  onHealthChanged?: () => void
}

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** SSH fingerprints read like `ssh-keygen -lf` output; RDP ones are certificate thumbprints. */
function formatFingerprint(key: TrustedKey): string {
  return key.protocol === 'Ssh' ? `SHA256:${key.fingerprint}` : key.fingerprint
}

function describeKey(key: TrustedKey): string {
  return key.protocol === 'Rdp' ? 'RDP TLS certificate (SHA-256)' : `SSH ${key.algorithm} host key`
}

/** Read-only overview of a host: addresses, tags, services and credentials (never secrets). */
export function HostDetails({
  host,
  onEdit,
  onDeleted,
  onTagClick,
  onConnectMenu,
  onChanged,
  allHosts,
  onShowHost,
  checks,
  onHealthChanged,
}: HostDetailsProps) {
  const [checking, setChecking] = useState(false)
  const [wake, setWake] = useState<{ busy: boolean; message?: string; error?: boolean }>({ busy: false })

  async function checkNow() {
    setChecking(true)
    try {
      await healthApi.check(host.id)
      onHealthChanged?.()
    } finally {
      setChecking(false)
    }
  }

  async function sendWake() {
    setWake({ busy: true })
    try {
      const result = await hostsApi.wake(host.id)
      setWake({ busy: false, message: `Wake-on-LAN sent to ${host.macAddress} (via ${result.sentTo.join(', ')}). The host may take a minute to come up.` })
    } catch (err) {
      setWake({ busy: false, error: true, message: err instanceof ApiError ? err.message : 'The Wake-on-LAN packet couldn’t be sent.' })
    }
  }

  const [deleting, setDeleting] = useState(false)
  const [forgetting, setForgetting] = useState(false)
  const [confirmForget, setConfirmForget] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleForgetKeys() {
    setForgetting(true)
    setError(null)
    try {
      await hostsApi.forgetTrustedKeys(host.id)
      onChanged({ ...host, trustedKeys: [] })
    } catch (err) {
      setError(toFormErrors(err).message)
    } finally {
      setForgetting(false)
      setConfirmForget(false)
    }
  }

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
  const hypervisor = host.hypervisorId ? allHosts.find((h) => h.id === host.hypervisorId) : undefined
  const guests = allHosts.filter((h) => h.hypervisorId === host.id).sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }))

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
          {host.canManage ? (
            <>
              <DeleteButton question="Delete this host?" onConfirm={handleDelete} loading={deleting} />
              <Button variant="secondary" onClick={onEdit} disabled={deleting}>
                Edit
              </Button>
            </>
          ) : null}
          {host.macAddress ? (
            <Button variant="secondary" onClick={() => void sendWake()} loading={wake.busy} disabled={deleting} title="Send a Wake-on-LAN packet">
              Wake
            </Button>
          ) : null}
          <Button
            onClick={(event) => onConnectMenu(event.currentTarget.getBoundingClientRect())}
            disabled={deleting}
            aria-haspopup="menu"
          >
            Connect
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
            {host.cpuCores || host.memoryMiB ? (
              <>
                <dt>{host.isVirtualMachine ? 'Resources' : 'Hardware'}</dt>
                <dd>
                  {[
                    host.cpuCores ? `${host.cpuCores} ${host.isVirtualMachine ? 'vCPU' : 'core'}${host.cpuCores === 1 ? '' : 's'}` : null,
                    host.memoryMiB ? formatMemory(host.memoryMiB) : null,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </dd>
              </>
            ) : null}
            {host.serialNumber ? (
              <>
                <dt>Serial number</dt>
                <dd className={styles.mono}>{host.serialNumber}</dd>
              </>
            ) : null}
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
            {host.jumpHostId ? (
              <>
                <dt>Reached through</dt>
                <dd>
                  {allHosts.find((h) => h.id === host.jumpHostId) ? (
                    <button type="button" className={styles.link} onClick={() => onShowHost(host.jumpHostId!)}>
                      {allHosts.find((h) => h.id === host.jumpHostId)!.name}
                    </button>
                  ) : (
                    <span className={styles.muted}>A jump host you can’t see</span>
                  )}
                  <span className={styles.muted}> · over SSH</span>
                </dd>
              </>
            ) : null}
            {host.isVirtualMachine ? (
              <>
                <dt>Runs on</dt>
                <dd>
                  {hypervisor ? (
                    <button type="button" className={styles.link} onClick={() => onShowHost(hypervisor.id)}>
                      {hypervisor.name}
                    </button>
                  ) : (
                    <span className={styles.muted}>{host.hypervisorId ? 'A host you can’t see' : 'Unknown (virtual machine)'}</span>
                  )}
                  {host.hypervisorSeenAtUtc ? (
                    <span className={styles.muted}>
                      {' '}
                      · seen there <time dateTime={host.hypervisorSeenAtUtc}>{dateTimeFormat.format(new Date(host.hypervisorSeenAtUtc))}</time>
                    </span>
                  ) : null}
                </dd>
              </>
            ) : null}
          </dl>
        </section>

        {guests.length > 0 ? (
          <section className={styles.section} aria-labelledby="guests-title">
            <h2 id="guests-title" className={styles.sectionTitle}>
              Virtual machines <span className={styles.count}>{guests.length}</span>
            </h2>
            <ul className={styles.guests}>
              {guests.map((guest) => (
                <li key={guest.id}>
                  <button type="button" className={styles.link} onClick={() => onShowHost(guest.id)}>
                    {guest.name}
                  </button>
                  <span className={`${styles.mono} ${styles.muted}`}> {guest.ipAddress ?? guest.hostname}</span>
                </li>
              ))}
            </ul>
          </section>
        ) : null}

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
          <div className={styles.sectionHeader}>
            <h2 id="services-title" className={styles.sectionTitle}>
              Services
            </h2>
            {host.services.length > 0 ? (
              <Button variant="ghost" size="sm" onClick={() => void checkNow()} loading={checking}>
                Check now
              </Button>
            ) : null}
          </div>
          {wake.message ? <Alert variant={wake.error ? 'error' : 'info'}>{wake.message}</Alert> : null}
          {host.services.length === 0 ? (
            <p className={styles.muted}>No services. Edit the host to add SSH, RDP, VNC and more.</p>
          ) : (
            <ul className={styles.rows}>
              {host.services.map((service, index) => {
                const info = PROTOCOL_INFO[service.protocol]
                return (
                  <li key={index} className={styles.row}>
                    <Badge variant={index === 0 ? 'accent' : 'neutral'}>{info.label}</Badge>
                    <span className={styles.rowMain}>
                      {service.label ?? info.name}
                      {service.protocol === 'Rdp' && service.rdpSecurity ? (
                        <span className={styles.muted}> · {RDP_SECURITY_INFO[service.rdpSecurity].label}</span>
                      ) : null}
                      {!isSupportedProtocol(service.protocol) ? (
                        <span className={styles.muted}> · Not supported: removed when the host is next edited</span>
                      ) : null}
                    </span>
                    <span className={`${styles.mono} ${styles.muted}`}>
                      {service.protocol === 'Idrac' && service.address ? `${service.address}, ` : ''}
                      {service.port ? `port ${service.port}` : info.defaultPort ? `port ${info.defaultPort} (default)` : ''}
                    </span>
                    <ServiceStatus check={checkFor(checks, service)} />
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

        {host.trustedKeys.length > 0 ? (
          <section className={styles.section} aria-labelledby="host-keys-title">
            <h2 id="host-keys-title" className={styles.sectionTitle}>
              Trusted keys and certificates
            </h2>
            <ul className={styles.rows}>
              {host.trustedKeys.map((key) => (
                <li key={`${key.protocol}-${key.port}-${key.algorithm}`} className={styles.row}>
                  <div className={styles.rowMain}>
                    <div className={`${styles.mono} ${styles.fingerprint}`}>{formatFingerprint(key)}</div>
                    <div className={styles.muted}>
                      {describeKey(key)} · port {key.port} · trusted {dateTimeFormat.format(new Date(key.trustedAtUtc))}
                    </div>
                  </div>
                </li>
              ))}
            </ul>
            <div className={styles.actions} hidden={!host.canManage}>
              {confirmForget ? (
                <>
                  <span className={styles.muted}>The next connection will ask you to verify the key again.</span>
                  <Button variant="danger" size="sm" onClick={handleForgetKeys} loading={forgetting}>
                    Forget keys
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => setConfirmForget(false)} disabled={forgetting}>
                    Keep
                  </Button>
                </>
              ) : (
                <Button variant="secondary" size="sm" onClick={() => setConfirmForget(true)}>
                  Forget trusted keys…
                </Button>
              )}
            </div>
          </section>
        ) : null}
      </div>
    </article>
  )
}

const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** A service's latest health check: up (and how fast), or down since when, plus its certificate's expiry. */
function ServiceStatus({ check }: { check: ServiceCheck | undefined }) {
  if (!check) {
    return null
  }
  const days = check.certificateExpiresUtc ? daysUntil(check.certificateExpiresUtc) : null
  return (
    <span className={styles.status} title={`Checked ${timeFormat.format(new Date(check.checkedAtUtc))}`}>
      {check.isUp ? (
        <span className={styles.up}>Up{check.latencyMs !== null ? ` · ${check.latencyMs} ms` : ''}</span>
      ) : (
        <span className={styles.down}>
          Down{check.downSinceUtc ? ` since ${timeFormat.format(new Date(check.downSinceUtc))}` : ''}
          {check.error ? ` · ${check.error}` : ''}
        </span>
      )}
      {days !== null ? (
        <span className={days <= CERTIFICATE_WARNING_DAYS ? styles.down : styles.muted} title={check.certificateSubject ?? undefined}>
          {' · '}
          {days < 0 ? `Certificate expired ${-days} days ago` : `Certificate expires in ${days} days`}
        </span>
      ) : null}
    </span>
  )
}
