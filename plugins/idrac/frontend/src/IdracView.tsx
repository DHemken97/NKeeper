import { useEffect, useRef, useState } from 'react'
import {
  api,
  ApiError,
  Button,
  Dialog,
  EndedCard,
  formatMemory,
  hostsApi,
  OpeningCard,
  SessionStatusBar,
  styles as nkeeper,
  toHostDraft,
  toHostInput,
  TrustCard,
  type Host,
  type HostKeyMessage,
  type RemoteSession,
  type SessionPhase,
} from '@nkeeper/plugin-sdk'
import { idracHardware, idracRequestBody, POWER_ACTIONS, type IdracMode, type IdracOverview, type IdracOverviewResponse } from './idrac'
import styles from './IdracView.module.css'

const sessionStyles = nkeeper.session

interface IdracViewProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
  /** Opens the iDRAC's console, RACADM or web interface in its own tab, with the same login. */
  onOpen: (mode: IdracMode | 'web') => void
  /** The host as NKeeper has it, to save the hardware details to (when the user may manage it). */
  host?: Host
  onHostChanged?: (host: Host) => void
}

/**
 * A server's status and power, read from its iDRAC over Redfish, with buttons that open the
 * iDRAC's virtual console, RACADM and web interface. Power actions other than "Power on" are
 * confirmed first.
 */
export function IdracView({ session, active, onShowHost, onOpen, host, onHostChanged }: IdracViewProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [overview, setOverview] = useState<IdracOverview | null>(null)
  const [refreshing, setRefreshing] = useState(true)
  const [attempt, setAttempt] = useState(0)
  const [confirming, setConfirming] = useState<(typeof POWER_ACTIONS)[number] | null>(null)
  const [power, setPower] = useState<{ busy: boolean; message?: string; error?: string }>({ busy: false })
  const [saving, setSaving] = useState<{ busy: boolean; message?: string; error?: string }>({ busy: false })
  const trustRef = useRef<string | undefined>(undefined)
  const targetRef = useRef<string | undefined>(undefined)

  useEffect(() => {
    let cancelled = false
    api
      .post<IdracOverviewResponse>('/api/plugins/idrac/overview', { body: idracRequestBody(session.request, trustRef.current) })
      .then(
        (response) => {
          if (cancelled) {
            return
          }
          const target = response.target ?? undefined
          targetRef.current = target
          if (response.status === 'trustRequired' && response.certificate) {
            const prompt: HostKeyMessage = {
              type: 'hostKey',
              status: 'unknown',
              kind: 'certificate',
              port: Number(new URL(response.target ?? 'https://x/').port || 443),
              algorithm: 'TLS certificate',
              fingerprint: response.certificate.fingerprint,
              subject: response.certificate.subject,
              expires: response.certificate.expires,
            }
            setPhase({ kind: 'hostKey', prompt, target })
            return
          }
          setOverview(response.overview)
          setPhase({ kind: 'connected', target: target ?? session.title })
        },
        (error: unknown) => {
          if (!cancelled) {
            setPhase({
              kind: 'ended',
              target: targetRef.current,
              code: error instanceof ApiError ? error.code : undefined,
              message: error instanceof ApiError ? error.message : 'Couldn’t reach the iDRAC.',
            })
          }
        },
      )
      .finally(() => {
        if (!cancelled) {
          setRefreshing(false)
        }
      })
    return () => {
      cancelled = true
    }
  }, [session.request, session.title, attempt])

  function refresh() {
    setRefreshing(true)
    if (phase.kind !== 'connected') {
      setPhase({ kind: 'connecting', target: targetRef.current })
    }
    setAttempt((n) => n + 1)
  }

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      setPhase({ kind: 'ended', target: phase.target, code: 'host_key_rejected', message: 'The certificate was not trusted, so NKeeper didn’t connect.' })
      return
    }
    trustRef.current = phase.prompt.fingerprint
    refresh()
  }

  async function press(action: (typeof POWER_ACTIONS)[number]) {
    setConfirming(null)
    setPower({ busy: true })
    try {
      await api.post('/api/plugins/idrac/power', { body: { ...idracRequestBody(session.request), resetType: action.type } })
      setPower({ busy: false, message: `${action.label}: sent to the server.` })
      refresh()
    } catch (error) {
      setPower({ busy: false, error: error instanceof ApiError ? error.message : `${action.label} failed.` })
    }
  }

  /** Saves the service tag, threads and memory to the host, as an ordinary edit. */
  async function saveHardware(hardware: ReturnType<typeof idracHardware>) {
    if (!host) {
      return
    }
    setSaving({ busy: true })
    try {
      const saved = await hostsApi.update(host.id, {
        ...toHostInput(toHostDraft(host), host.version),
        serialNumber: hardware.serialNumber ?? host.serialNumber,
        cpuCores: hardware.cpuCores ?? host.cpuCores,
        memoryMiB: hardware.memoryMiB ?? host.memoryMiB,
      })
      onHostChanged?.(saved)
      setSaving({ busy: false, message: 'Saved to the host.' })
    } catch (error) {
      setSaving({ busy: false, error: error instanceof ApiError ? error.message : 'Couldn’t save to the host.' })
    }
  }

  const hardware = overview ? idracHardware(overview) : null
  const hardwareChanges = hardware && host
    ? [
        hardware.serialNumber && hardware.serialNumber !== host.serialNumber ? `serial number ${hardware.serialNumber}` : null,
        hardware.cpuCores && hardware.cpuCores !== host.cpuCores ? `${hardware.cpuCores} CPU cores` : null,
        hardware.memoryMiB && hardware.memoryMiB !== host.memoryMiB ? `${formatMemory(hardware.memoryMiB)} memory` : null,
      ].filter((change): change is string => change !== null)
    : []

  const on = overview?.powerState?.toLowerCase().startsWith('on') ?? false
  const actions = overview ? POWER_ACTIONS.filter((a) => overview.powerActions.includes(a.type)) : []

  return (
    <div className={sessionStyles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} iDRAC`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <Button variant="ghost" size="sm" onClick={refresh} disabled={refreshing}>
            {refreshing ? 'Refreshing…' : 'Refresh'}
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={`${sessionStyles.terminalArea} ${sessionStyles.webArea}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={refresh} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
        {phase.kind === 'connected' && overview ? (
          <div className={styles.panel}>
            <section className={styles.section} aria-labelledby={`${session.id}-server`}>
              <h3 id={`${session.id}-server`} className={styles.heading}>
                {[overview.manufacturer, overview.model].filter(Boolean).join(' ') || 'Server'}
              </h3>
              <div className={styles.state}>
                <span>
                  <span className={`${sessionStyles.dot} ${on ? sessionStyles.connected : ''}`} /> Power {overview.powerState ?? 'unknown'}
                </span>
                {overview.health ? (
                  <span className={`${styles.health} ${styles[`health${overview.health}`] ?? ''}`}>Health {overview.health}</span>
                ) : null}
              </div>
              <dl className={styles.facts}>
                <Fact label="Service tag" value={overview.serviceTag} />
                <Fact label="Serial number" value={overview.serialNumber} />
                <Fact label="Host name" value={overview.hostName} />
                <Fact
                  label="Processors"
                  value={
                    [
                      overview.processorCount ? `${overview.processorCount} × ${overview.processorModel ?? 'CPU'}` : overview.processorModel,
                      overview.logicalProcessorCount ? `${overview.logicalProcessorCount} threads` : null,
                    ]
                      .filter(Boolean)
                      .join(' · ') || null
                  }
                />
                <Fact label="Memory" value={overview.memoryGiB ? `${overview.memoryGiB} GiB` : null} />
                <Fact label="BIOS" value={overview.biosVersion} />
                <Fact label="iDRAC" value={[overview.managerModel, overview.firmwareVersion].filter(Boolean).join(' · ') || null} />
              </dl>
              {host?.canManage && hardware && hardwareChanges.length > 0 ? (
                <div className={styles.buttons}>
                  <Button size="sm" variant="secondary" loading={saving.busy} onClick={() => void saveHardware(hardware)}>
                    Save to host
                  </Button>
                  <span className={sessionStyles.hint}>Records the {hardwareChanges.join(', ')} on {host.name}.</span>
                </div>
              ) : null}
              {saving.message ? (
                <p className={sessionStyles.hint} role="status">
                  {saving.message}
                </p>
              ) : null}
              {saving.error ? (
                <p className={styles.error} role="alert">
                  {saving.error}
                </p>
              ) : null}
            </section>

            <section className={styles.section} aria-labelledby={`${session.id}-open`}>
              <h3 id={`${session.id}-open`} className={styles.heading}>
                Open
              </h3>
              <div className={styles.buttons}>
                <Button size="sm" onClick={() => onOpen('console')} disabled={!overview.console.available}>
                  Virtual console
                </Button>
                <Button size="sm" variant="secondary" onClick={() => onOpen('racadm')}>
                  RACADM
                </Button>
                <Button size="sm" variant="secondary" onClick={() => onOpen('web')}>
                  Web interface
                </Button>
              </div>
              {overview.console.problem ? <p className={sessionStyles.hint}>{overview.console.problem}</p> : null}
            </section>

            {actions.length > 0 ? (
              <section className={styles.section} aria-labelledby={`${session.id}-power`}>
                <h3 id={`${session.id}-power`} className={styles.heading}>
                  Power
                </h3>
                <div className={styles.buttons}>
                  {actions.map((action) => (
                    <Button
                      key={action.type}
                      size="sm"
                      variant="secondary"
                      disabled={power.busy || (action.when === 'on') !== on}
                      onClick={() => (action.confirm ? setConfirming(action) : void press(action))}
                    >
                      {action.label}
                    </Button>
                  ))}
                </div>
                {power.message ? (
                  <p className={sessionStyles.hint} role="status">
                    {power.message}
                  </p>
                ) : null}
                {power.error ? (
                  <p className={styles.error} role="alert">
                    {power.error}
                  </p>
                ) : null}
              </section>
            ) : null}
          </div>
        ) : null}
      </div>

      {confirming ? (
        <Dialog
          title={`${confirming.label} ${session.title}?`}
          onClose={() => setConfirming(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setConfirming(null)}>
                Cancel
              </Button>
              <Button variant="danger" onClick={() => void press(confirming)} data-autofocus>
                {confirming.label}
              </Button>
            </>
          }
        >
          <p>
            This {confirming.confirm}. Anyone using the server will be disconnected.
          </p>
        </Dialog>
      ) : null}
    </div>
  )
}

function Fact({ label, value }: { label: string; value: string | null }) {
  return value ? (
    <>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </>
  ) : null
}
