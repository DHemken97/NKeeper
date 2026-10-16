import { useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { api, ApiError } from '../../../lib/api'
import { hostKeyPrompt, kvmRequestBody, type HostKeyPrompt, type VirtualMachine } from '../kvm'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface VirtualMachinesViewProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
  /** Opens a VM's console in its own tab. */
  onOpenConsole: (vm: VirtualMachine) => void
}

interface ListResponse {
  status: 'ready' | 'trustRequired'
  vms: VirtualMachine[] | null
  hostKey: HostKeyPrompt | null
}

/**
 * The virtual machines of a KVM host (libvirt over SSH, like virt-manager), with a console button
 * for each running VM that has a SPICE display.
 */
export function VirtualMachinesView({ session, active, onShowHost, onOpenConsole }: VirtualMachinesViewProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [vms, setVms] = useState<VirtualMachine[] | null>(null)
  const [refreshing, setRefreshing] = useState(true)
  const [attempt, setAttempt] = useState(0)
  const trustRef = useRef<string | undefined>(undefined)
  const target = session.title

  useEffect(() => {
    let cancelled = false
    api
      .post<ListResponse>('/api/kvm/vms', { body: kvmRequestBody(session.request, trustRef.current) })
      .then(
        (response) => {
          if (cancelled) {
            return
          }
          if (response.status === 'trustRequired' && response.hostKey) {
            setPhase({ kind: 'hostKey', prompt: hostKeyPrompt(response.hostKey), target })
            return
          }
          setVms(response.vms ?? [])
          setPhase({ kind: 'connected', target })
        },
        (error: unknown) => {
          if (!cancelled) {
            setPhase({
              kind: 'ended',
              target,
              code: error instanceof ApiError ? error.code : undefined,
              message: error instanceof ApiError ? error.message : 'Couldn’t list the virtual machines.',
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
  }, [session.request, target, attempt])

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      setPhase({ kind: 'ended', target, code: 'host_key_rejected', message: 'The host key was not trusted, so NKeeper didn’t connect.' })
      return
    }
    trustRef.current = phase.prompt.fingerprint
    refresh()
  }

  function refresh() {
    setRefreshing(true)
    if (phase.kind !== 'connected') {
      setPhase({ kind: 'connecting', target })
    }
    setAttempt((n) => n + 1)
  }

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} virtual machines`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <Button variant="ghost" size="sm" onClick={refresh} disabled={refreshing}>
            {refreshing ? 'Refreshing…' : 'Refresh'}
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={`${styles.terminalArea} ${styles.webArea}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={refresh} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
        {phase.kind === 'connected' && vms ? (
          <div className={styles.vmList}>
            {vms.length === 0 ? (
              <p className={styles.vmEmpty}>This host has no virtual machines that this account can see.</p>
            ) : (
              <table className={styles.vmTable}>
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col">State</th>
                    <th scope="col">Console</th>
                    <th scope="col">
                      <span className="visually-hidden">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {vms.map((vm) => {
                    const running = vm.state === 'running'
                    const spice = vm.display === 'spice'
                    return (
                      <tr key={vm.uuid}>
                        <td className={styles.vmName}>{vm.name}</td>
                        <td>
                          <span className={`${styles.dot} ${running ? styles.connected : ''}`} /> {vm.state}
                        </td>
                        <td>{vm.display ? vm.display.toUpperCase() : '—'}</td>
                        <td className={styles.vmActions}>
                          {running && spice ? (
                            <Button size="sm" variant="secondary" onClick={() => onOpenConsole(vm)}>
                              Open console
                            </Button>
                          ) : running && vm.display ? (
                            <span className={styles.hint}>Only SPICE consoles are supported</span>
                          ) : null}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            )}
          </div>
        ) : null}
      </div>
    </div>
  )
}
