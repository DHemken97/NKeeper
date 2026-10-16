import { useEffect, useId, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { api, ApiError, apiWebSocketUrl } from '../../../lib/api'
import { SpiceMainConn, sendCtrlAltDel } from '../../../vendor/spice-html5/main.js'
import { hostKeyPrompt, kvmRequestBody, type HostKeyPrompt } from '../kvm'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface SpiceConsoleProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
}

interface ConsoleResponse {
  status: 'ready' | 'trustRequired'
  sessionId: string | null
  password: string | null
  hostKey: HostKeyPrompt | null
}

const RESIZE_DEBOUNCE_MS = 250

/** Why the SPICE client stopped, in words for the user. */
function describeSpiceError(error: unknown, wasConnected: boolean): string {
  const text = error instanceof Error ? error.message : ''
  if (text === 'Bad password.') {
    return 'The virtual machine rejected the console password.'
  }
  if (wasConnected) {
    return 'The console closed. The virtual machine may have shut down or restarted.'
  }
  return text ? `Couldn’t open the console: ${text}` : 'Couldn’t open the console.'
}

/**
 * A virtual machine's SPICE console (like virt-manager's), drawn by spice-html5. NKeeper's server
 * tunnels every SPICE channel through its SSH connection to the KVM host. The display is shown at
 * the VM's own resolution; with the guest agent (spice-vdagent) it follows the size of the tab.
 */
export function SpiceConsole({ session, active, onShowHost }: SpiceConsoleProps) {
  const screenId = `spice-screen-${useId().replace(/[^a-zA-Z0-9]/g, '')}`
  const areaRef = useRef<HTMLDivElement>(null)
  const scrollerRef = useRef<HTMLDivElement>(null)
  const connectionRef = useRef<SpiceMainConn | null>(null)
  const trustRef = useRef<string | undefined>(undefined)
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)
  const target = session.request.vm?.name ?? session.title

  useEffect(() => {
    const scroller = scrollerRef.current!
    let cancelled = false
    let sessionId: string | null = null
    let connection: SpiceMainConn | null = null
    let connected = false
    let agent = false

    const stop = () => {
      connection?.stop()
      connection = null
      connectionRef.current = null
    }

    const fail = (message: string, code?: string) => {
      stop()
      setPhase({ kind: 'ended', message, code, target })
    }

    /** Asks the guest agent to match the VM's resolution to the tab (X needs multiples of 8). */
    const fitToTab = () => {
      if (connection && agent && scroller.clientWidth > 0 && scroller.clientHeight > 0) {
        const width = scroller.clientWidth - (scroller.clientWidth % 8)
        const height = scroller.clientHeight - (scroller.clientHeight % 8)
        connection.resize_window(0, width, height, 32, 0, 0)
      }
    }

    api.post<ConsoleResponse>('/api/spice/sessions', { body: kvmRequestBody(session.request, trustRef.current) }).then(
      (response) => {
        if (cancelled) {
          if (response.sessionId) {
            void api.delete(`/api/spice/sessions/${response.sessionId}`).catch(() => {})
          }
          return
        }
        if (response.status === 'trustRequired' && response.hostKey) {
          setPhase({ kind: 'hostKey', prompt: hostKeyPrompt(response.hostKey), target })
          return
        }

        sessionId = response.sessionId!
        try {
          connection = new SpiceMainConn({
            uri: apiWebSocketUrl(`/api/spice/channel/${sessionId}`),
            screen_id: screenId,
            password: response.password ?? '',
            onsuccess: () => {
              connected = true
              setPhase({ kind: 'connected', target })
            },
            onagent: () => {
              agent = true
              fitToTab()
            },
            onerror: (error) => fail(describeSpiceError(error, connected)),
          })
          connectionRef.current = connection
        } catch (error) {
          fail(describeSpiceError(error, false))
        }
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

    let resizeTimer: number | undefined
    const observer = new ResizeObserver(() => {
      window.clearTimeout(resizeTimer)
      resizeTimer = window.setTimeout(fitToTab, RESIZE_DEBOUNCE_MS)
    })
    observer.observe(scroller)

    return () => {
      cancelled = true
      observer.disconnect()
      window.clearTimeout(resizeTimer)
      stop()
      document.getElementById(screenId)?.replaceChildren()
      // Closing the tab ends the console (and its SSH tunnel) on the server.
      if (sessionId) {
        void api.delete(`/api/spice/sessions/${sessionId}`).catch(() => {})
      }
    }
  }, [session.request, screenId, target, attempt])

  // The active tab gets the keyboard (spice-html5 listens on the display canvas).
  useEffect(() => {
    if (active && phase.kind === 'connected') {
      focusDisplay()
    }
  }, [active, phase.kind])

  function focusDisplay() {
    scrollerRef.current?.querySelector('canvas')?.focus()
  }

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      setPhase({ kind: 'ended', target, code: 'host_key_rejected', message: 'The host key was not trusted, so NKeeper didn’t connect.' })
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
    if (connectionRef.current) {
      sendCtrlAltDel(connectionRef.current)
      focusDisplay()
    }
  }

  async function toggleFullscreen() {
    if (document.fullscreenElement) {
      await document.exitFullscreen()
      return
    }
    await areaRef.current?.requestFullscreen()
    const keyboard = (navigator as Navigator & { keyboard?: { lock?: () => Promise<void> } }).keyboard
    await keyboard?.lock?.().catch(() => {})
    focusDisplay()
  }

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${target} console`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <>
            <Button variant="ghost" size="sm" onClick={ctrlAltDel}>
              Ctrl+Alt+Del
            </Button>
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
        <div ref={scrollerRef} className={styles.spiceScroller} onMouseDown={focusDisplay}>
          <div id={screenId} className={styles.spiceScreen} />
        </div>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={reconnect} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
      </div>
    </div>
  )
}
