import { Link } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { healthApi, hostsApi } from '../../inventory/api/inventoryApi'
import { useResource } from '../../inventory/hooks/useResource'
import { CERTIFICATE_WARNING_DAYS, daysUntil, PROTOCOL_INFO, type Host, type ServiceCheck } from '../../inventory/types'
import styles from './Reports.module.css'

const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

/** "SSH:22", or "iDRAC 10.0.10.20:443". */
function serviceName(check: ServiceCheck): string {
  return `${PROTOCOL_INFO[check.protocol].label}${check.address ? ` ${check.address}` : ''}:${check.port}`
}

/**
 * The health of every service, as the NKeeper server sees it: what's down (and since when), and
 * which TLS certificates (HTTPS, iDRAC, RDP) have expired or soon will.
 */
export function HealthReport() {
  const hosts = useResource((signal) => hostsApi.list(signal))
  const health = useResource((signal) => healthApi.list(signal))
  const error = hosts.error ?? health.error

  if (error && (!hosts.data || !health.data)) {
    return (
      <div className={styles.state}>
        <Alert>{toFormErrors(error).message}</Alert>
      </div>
    )
  }

  if (!hosts.data || !health.data) {
    return (
      <div className={styles.state}>
        <Spinner label="Loading health" />
      </div>
    )
  }

  const byId = new Map(hosts.data.map((h) => [h.id, h]))
  const checks = health.data.filter((c) => byId.has(c.hostId))
  const down = checks
    .filter((c) => !c.isUp)
    .sort((a, b) => (a.downSinceUtc ?? a.checkedAtUtc).localeCompare(b.downSinceUtc ?? b.checkedAtUtc))
  const certificates = checks
    .filter((c): c is ServiceCheck & { certificateExpiresUtc: string } => c.certificateExpiresUtc !== null)
    .sort((a, b) => a.certificateExpiresUtc.localeCompare(b.certificateExpiresUtc))
  const expiring = certificates.filter((c) => daysUntil(c.certificateExpiresUtc) <= CERTIFICATE_WARNING_DAYS).length
  const unchecked = hosts.data.filter((h) => h.services.length > 0 && !checks.some((c) => c.hostId === h.id)).length
  const latest = checks.reduce<string | null>((last, c) => (last === null || c.checkedAtUtc > last ? c.checkedAtUtc : last), null)

  const reload = () => {
    hosts.reload()
    health.reload()
  }

  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>Health</h2>
          <p className={styles.reportSummary}>
            {checks.length - down.length} of {checks.length} services up · {down.length} down · {expiring}{' '}
            {expiring === 1 ? 'certificate' : 'certificates'} expiring within {CERTIFICATE_WARNING_DAYS} days
            {unchecked > 0 ? ` · ${unchecked} ${unchecked === 1 ? 'host' : 'hosts'} not checked yet` : ''}
            {latest ? ` · last check ${timeFormat.format(new Date(latest))}` : ''}
          </p>
        </div>
        <div className={styles.reportActions}>
          <Button size="sm" variant="secondary" onClick={reload} disabled={hosts.loading || health.loading}>
            Refresh
          </Button>
        </div>
      </header>

      <div className={styles.tableScroll}>
        <section className={styles.healthSection} aria-labelledby="down-title">
          <h3 id="down-title" className={styles.healthTitle}>
            Down
          </h3>
          {down.length === 0 ? (
            <p className={styles.healthEmpty}>Every checked service is answering.</p>
          ) : (
            <table className={styles.healthTable}>
              <thead>
                <tr>
                  <th scope="col">Host</th>
                  <th scope="col">Service</th>
                  <th scope="col">Down since</th>
                  <th scope="col">Why</th>
                </tr>
              </thead>
              <tbody>
                {down.map((c) => (
                  <tr key={`${c.hostId}-${c.protocol}-${c.port}-${c.address}`}>
                    <td>
                      <HostLink host={byId.get(c.hostId)!} />
                    </td>
                    <td className={styles.mono}>{serviceName(c)}</td>
                    <td>{timeFormat.format(new Date(c.downSinceUtc ?? c.checkedAtUtc))}</td>
                    <td className={styles.overcommitted}>{c.error}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        <section className={styles.healthSection} aria-labelledby="certificates-title">
          <h3 id="certificates-title" className={styles.healthTitle}>
            Certificates
          </h3>
          {certificates.length === 0 ? (
            <p className={styles.healthEmpty}>No HTTPS, iDRAC or RDP certificates have been read yet.</p>
          ) : (
            <table className={styles.healthTable}>
              <thead>
                <tr>
                  <th scope="col">Host</th>
                  <th scope="col">Service</th>
                  <th scope="col">Issued to</th>
                  <th scope="col">Expires</th>
                </tr>
              </thead>
              <tbody>
                {certificates.map((c) => {
                  const days = daysUntil(c.certificateExpiresUtc)
                  return (
                    <tr key={`${c.hostId}-${c.protocol}-${c.port}-${c.address}`}>
                      <td>
                        <HostLink host={byId.get(c.hostId)!} />
                      </td>
                      <td className={styles.mono}>{serviceName(c)}</td>
                      <td>{c.certificateSubject}</td>
                      <td className={days <= CERTIFICATE_WARNING_DAYS ? styles.overcommitted : undefined}>
                        {dateFormat.format(new Date(c.certificateExpiresUtc))} ({days < 0 ? `expired ${-days} days ago` : `in ${days} days`})
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )}
        </section>
      </div>
    </>
  )
}

function HostLink({ host }: { host: Host }) {
  return (
    <Link to={`/?host=${host.id}`} className={styles.hostLink}>
      {host.name}
    </Link>
  )
}
