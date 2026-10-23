import { useEffect, useRef, useState } from 'react'
import { api, ApiError } from '../../../lib/api'
import { hostKeyPrompt, kvmRequestBody, type HostKeyPrompt, type VirtualMachine } from '../kvm'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface KvmVmConsoleProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
  /** The VM is running with a display: the tab becomes that console. */
  onDisplay: (display: 'spice' | 'vnc') => void
}

interface VmResponse {
  status: 'ready' | 'trustRequired'
  vm: VirtualMachine | null
  hostKey: HostKeyPrompt | null
}

/**
 * Connect ▸ KVM console on a libvirt VM's own host: asks its hypervisor (over SSH) which display the
 * VM has, then hands the tab to its SPICE or VNC console.
 */
export function KvmVmConsole({ session, active, onShowHost, onDisplay }: KvmVmConsoleProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)
  const trustRef = useRef<string | undefined>(undefined)
  const onDisplayRef = useRef(onDisplay)
  const target = session.title

  useEffect(() => {
    onDisplayRef.current = onDisplay
  })

  useEffect(() => {
    let cancelled = false
    api.post<VmResponse>('/api/kvm/vm', { body: kvmRequestBody(session.request, trustRef.current) }).then(
      (response) => {
        if (cancelled) {
          return
        }
        if (response.status === 'trustRequired' && response.hostKey) {
          setPhase({ kind: 'hostKey', prompt: hostKeyPrompt(response.hostKey), target })
          return
        }
        const vm = response.vm
        if (vm?.state !== 'running') {
          setPhase({ kind: 'ended', target, message: `“${target}” is not running (${vm?.state ?? 'unknown'}). Start it, then open its console.` })
        } else if (vm.display !== 'spice' && vm.display !== 'vnc') {
          setPhase({ kind: 'ended', target, message: `“${target}” has no SPICE or VNC display.` })
        } else {
          onDisplayRef.current(vm.display)
        }
      },
      (error: unknown) => {
        if (!cancelled) {
          setPhase({
            kind: 'ended',
            target,
            code: error instanceof ApiError ? error.code : undefined,
            message: error instanceof ApiError ? error.message : 'Couldn’t reach the KVM host.',
          })
        }
      },
    )
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
    retry()
  }

  function retry() {
    setPhase({ kind: 'connecting', target })
    setAttempt((n) => n + 1)
  }

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${target} console`}>
      <SessionStatusBar phase={phase} />
      <div className={styles.terminalArea}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={retry} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
      </div>
    </div>
  )
}
