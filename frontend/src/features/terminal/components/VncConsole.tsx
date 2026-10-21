import RFB from '@novnc/novnc'
import { useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { api, ApiError, apiWebSocketUrl } from '../../../lib/api'
import { certificatePrompt, hostKeyPrompt, kvmRequestBody, proxmoxConsoleBody, type CertificatePrompt, type HostKeyPrompt } from '../kvm'
import { typeIntoVnc } from '../typeText'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'
import { TypeClipboardButton } from './TypeClipboardButton'

interface VncConsoleProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
  /** How the server reaches the console: over SSH to a KVM host, or through the Proxmox API. */
  via?: 'kvm' | 'proxmox'
}

interface ConsoleResponse {
  status: 'ready' | 'trustRequired'
  sessionId: string | null
  password: string | null
  /** KVM: the SSH host key to trust. */
  hostKey?: HostKeyPrompt | null
  /** Proxmox: the node's HTTPS address, and its certificate to trust. */
  target?: string | null
  certificate?: CertificatePrompt | null
}

/**
 * A virtual machine's VNC console (like virt-manager's or Proxmox's), drawn by noVNC and scaled to
 * fit the tab. NKeeper's server relays the VNC connection: through its SSH connection to the KVM
 * host, or through the Proxmox API's console WebSocket, as Proxmox's own console does.
 */
export function VncConsole({ session, active, onShowHost, via = 'kvm' }: VncConsoleProps) {
  const areaRef = useRef<HTMLDivElement>(null)
  const screenRef = useRef<HTMLDivElement>(null)
  const rfbRef = useRef<RFB | null>(null)
  const trustRef = useRef<string | undefined>(undefined)
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)
  const target = session.request.vm?.name ?? session.title

  useEffect(() => {
    const screen = screenRef.current!
    let cancelled = false
    let sessionId: string | null = null
    let connected = false

    const stop = () => {
      const rfb = rfbRef.current
      rfbRef.current = null
      rfb?.disconnect()
    }

    const fail = (message: string) => {
      stop()
      if (!cancelled) {
        setPhase({ kind: 'ended', message, target })
      }
    }

    const open =
      via === 'proxmox'
        ? api.post<ConsoleResponse>('/api/proxmox/consoles', { body: proxmoxConsoleBody(session.request, trustRef.current) })
        : api.post<ConsoleResponse>('/api/vnc/consoles', { body: kvmRequestBody(session.request, trustRef.current) })
    open.then(
      (response) => {
        if (cancelled) {
          if (response.sessionId) {
            void api.delete(`/api/vnc/consoles/${response.sessionId}`).catch(() => {})
          }
          return
        }
        if (response.status === 'trustRequired' && response.hostKey) {
          setPhase({ kind: 'hostKey', prompt: hostKeyPrompt(response.hostKey), target })
          return
        }
        if (response.status === 'trustRequired' && response.certificate) {
          setPhase({ kind: 'hostKey', prompt: certificatePrompt(response.certificate, response.target ?? null), target: response.target ?? target })
          return
        }

        sessionId = response.sessionId!
        const password = response.password
        let rfb: RFB
        try {
          rfb = new RFB(screen, apiWebSocketUrl(`/api/vnc/channel/${sessionId}`), password ? { credentials: { password } } : {})
        } catch (error) {
          fail(error instanceof Error ? `Couldn’t open the console: ${error.message}` : 'Couldn’t open the console.')
          return
        }
        rfb.scaleViewport = true
        rfb.background = '#000'
        rfbRef.current = rfb
        rfb.addEventListener('connect', () => {
          connected = true
          setPhase({ kind: 'connected', target })
        })
        rfb.addEventListener('credentialsrequired', () =>
          fail(
            password || via === 'proxmox'
              ? 'The virtual machine rejected the console password.'
              : 'This VM’s VNC display needs a password, and libvirt didn’t give it to NKeeper. The SSH user needs full (read-write) access to libvirt to read it.',
          ),
        )
        rfb.addEventListener('securityfailure', () => fail('The virtual machine rejected the console password.'))
        rfb.addEventListener('disconnect', ({ detail }) => {
          // A disconnect we asked for (closing or reconnecting) isn't an error.
          if (rfbRef.current !== rfb) {
            return
          }
          rfbRef.current = null // Already disconnected.
          fail(
            connected
              ? 'The console closed. The virtual machine may have shut down or restarted.'
              : detail.clean
                ? 'Couldn’t open the console.'
                : 'Couldn’t open the console: the connection to the VM’s display failed.',
          )
        })
      },
      (error: unknown) => {
        if (!cancelled) {
          setPhase({
            kind: 'ended',
            target,
            code: error instanceof ApiError ? error.code : undefined,
            message: error instanceof ApiError ? error.message : 'Couldn’t open the console.',
          })
        }
      },
    )

    return () => {
      cancelled = true
      stop()
      // Closing the tab ends the console (and its SSH tunnel) on the server.
      if (sessionId) {
        void api.delete(`/api/vnc/consoles/${sessionId}`).catch(() => {})
      }
    }
  }, [session.request, target, attempt, via])

  // The active tab gets the keyboard.
  useEffect(() => {
    if (active && phase.kind === 'connected') {
      rfbRef.current?.focus({ preventScroll: true })
    }
  }, [active, phase.kind])

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      const what = phase.prompt.kind === 'certificate' ? 'certificate' : 'host key'
      setPhase({ kind: 'ended', target, code: 'host_key_rejected', message: `The ${what} was not trusted, so NKeeper didn’t connect.` })
      return
    }
    trustRef.current = phase.prompt.fingerprint
    reconnect()
  }

  function reconnect() {
    setPhase({ kind: 'connecting', target })
    setAttempt((n) => n + 1)
  }

  function ctrlAltDel() {
    rfbRef.current?.sendCtrlAltDel()
    rfbRef.current?.focus({ preventScroll: true })
  }

  async function toggleFullscreen() {
    if (document.fullscreenElement) {
      await document.exitFullscreen()
      return
    }
    await areaRef.current?.requestFullscreen()
    const keyboard = (navigator as Navigator & { keyboard?: { lock?: () => Promise<void> } }).keyboard
    await keyboard?.lock?.().catch(() => {})
    rfbRef.current?.focus({ preventScroll: true })
  }

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${target} console`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <>
            <Button variant="ghost" size="sm" onClick={ctrlAltDel}>
              Ctrl+Alt+Del
            </Button>
            <TypeClipboardButton
              type={(text, signal) => (rfbRef.current ? typeIntoVnc(rfbRef.current, text, signal) : Promise.reject(new Error('The console is closed.')))}
              onDone={() => rfbRef.current?.focus({ preventScroll: true })}
            />
            <Button variant="ghost" size="sm" onClick={() => void toggleFullscreen()}>
              Fullscreen
            </Button>
          </>
        ) : null}
        {phase.kind === 'ended' ? (
          <Button variant="secondary" size="sm" onClick={reconnect}>
            Reconnect
          </Button>
        ) : null}
      </SessionStatusBar>

      <div ref={areaRef} className={styles.terminalArea}>
        <div ref={screenRef} className={styles.vncScreen} />
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={reconnect} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
      </div>
    </div>
  )
}
