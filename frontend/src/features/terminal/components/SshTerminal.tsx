import { FitAddon } from '@xterm/addon-fit'
import { Terminal } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'
import { useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { apiWebSocketUrl } from '../../../lib/api'
import type { RemoteSession, ServerMessage, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface SshTerminalProps {
  session: RemoteSession
  /** Only the active tab is visible; it gets keyboard focus and is resized to fit. */
  active: boolean
  /** Show the host's details (e.g. to forget a changed host key). */
  onShowHost: (hostId: string) => void
  /** The remote shell ended normally (e.g. `exit`); the tab closes itself. Errors keep it open. */
  onEnded: () => void
}

const THEME = {
  background: '#0b1220',
  foreground: '#e5ebf5',
  cursor: '#8fb4ff',
  cursorAccent: '#0b1220',
  selectionBackground: 'rgba(37, 99, 235, 0.4)',
}

/** Fits the terminal to its container, unless the tab is hidden (zero size). */
function fitIfVisible(fit: FitAddon, element: HTMLElement | null) {
  if (element && element.offsetWidth > 0 && element.offsetHeight > 0) {
    try {
      fit.fit()
    } catch {
      // The renderer is not ready yet; the next resize will fit.
    }
  }
}

/**
 * One interactive SSH or Telnet session: an xterm.js terminal bridged to the server over a WebSocket.
 * Both speak the same messages; Telnet has no host key to trust and isn't encrypted to the host.
 */
export function SshTerminal({ session, active, onShowHost, onEnded }: SshTerminalProps) {
  const telnet = session.protocol === 'telnet'
  const containerRef = useRef<HTMLDivElement>(null)
  const terminalRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const socketRef = useRef<WebSocket | null>(null)
  const connectedRef = useRef(false)
  // One-off passwords are sent once and then forgotten.
  const requestRef = useRef(session.request)
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)
  // Read from socket handlers created per attempt, so always call the latest callback.
  const onEndedRef = useRef(onEnded)
  useEffect(() => {
    onEndedRef.current = onEnded
  })

  // The terminal lives as long as the tab, across reconnects.
  useEffect(() => {
    const container = containerRef.current!
    const terminal = new Terminal({
      cursorBlink: true,
      fontFamily: "ui-monospace, 'Cascadia Mono', Consolas, 'DejaVu Sans Mono', monospace",
      fontSize: 14,
      scrollback: 5000,
      theme: THEME,
    })
    const fit = new FitAddon()
    terminal.loadAddon(fit)
    terminal.open(container)
    fitIfVisible(fit, container)

    const encoder = new TextEncoder()
    const send = (data: Uint8Array<ArrayBuffer> | string) => {
      const socket = socketRef.current
      if (socket?.readyState === WebSocket.OPEN && connectedRef.current) {
        socket.send(data)
      }
    }
    const subscriptions = [
      terminal.onData((data) => send(encoder.encode(data))),
      // Some key sequences arrive as "binary" strings: one char per byte.
      terminal.onBinary((data) => send(Uint8Array.from(data, (c) => c.charCodeAt(0)))),
      terminal.onResize(({ cols, rows }) => send(JSON.stringify({ type: 'resize', cols, rows }))),
    ]
    const observer = new ResizeObserver(() => fitIfVisible(fit, container))
    observer.observe(container)

    terminalRef.current = terminal
    fitRef.current = fit
    return () => {
      observer.disconnect()
      subscriptions.forEach((s) => s.dispose())
      terminal.dispose()
    }
  }, [])

  // One WebSocket per connection attempt.
  useEffect(() => {
    const terminal = terminalRef.current!
    const request = requestRef.current
    const socket = new WebSocket(apiWebSocketUrl(telnet ? '/api/telnet/session' : '/api/ssh/session'))
    socket.binaryType = 'arraybuffer'
    socketRef.current = socket
    connectedRef.current = false
    let finished = false

    socket.onopen = () => {
      socket.send(
        JSON.stringify({
          type: 'start',
          hostId: request.hostId,
          serviceIndex: request.serviceIndex,
          credentialId: request.credentialId,
          username: request.oneOff?.username,
          password: request.oneOff?.password,
          cols: terminal.cols,
          rows: terminal.rows,
        }),
      )
      if (request.oneOff) {
        requestRef.current = { ...request, oneOff: undefined }
      }
    }

    socket.onmessage = (event: MessageEvent<ArrayBuffer | string>) => {
      if (typeof event.data !== 'string') {
        terminal.write(new Uint8Array(event.data))
        return
      }
      const message = JSON.parse(event.data) as ServerMessage
      switch (message.type) {
        case 'status':
          setPhase({ kind: 'connecting', target: message.target })
          break
        case 'hostKey':
          setPhase((current) => ({ kind: 'hostKey', prompt: message, target: current.target }))
          break
        case 'connected':
          connectedRef.current = true
          setPhase({ kind: 'connected', target: message.target })
          // The size may have changed while connecting.
          socket.send(JSON.stringify({ type: 'resize', cols: terminal.cols, rows: terminal.rows }))
          terminal.focus()
          break
        case 'error':
          finished = true
          setPhase((current) => ({ kind: 'ended', message: message.message, code: message.code, target: current.target }))
          break
        case 'closed':
          finished = true
          onEndedRef.current()
          break
      }
    }

    socket.onclose = () => {
      connectedRef.current = false
      if (!finished) {
        setPhase((current) => ({
          kind: 'ended',
          message: current.kind === 'connected' ? 'The connection was lost.' : 'Couldn’t reach the NKeeper server.',
          target: current.target,
        }))
      }
    }

    return () => {
      socket.onclose = null
      socket.onmessage = null
      socket.close()
    }
  }, [attempt, telnet])

  // Fit and focus when the tab becomes visible.
  useEffect(() => {
    if (active && fitRef.current) {
      fitIfVisible(fitRef.current, containerRef.current)
      if (connectedRef.current) {
        terminalRef.current?.focus()
      }
    }
  }, [active])

  function decideHostKey(trust: boolean) {
    socketRef.current?.send(JSON.stringify({ type: 'hostKeyDecision', trust }))
    setPhase((current) => ({ kind: 'connecting', target: current.target }))
  }

  function reconnect() {
    terminalRef.current?.write('\r\n')
    setPhase({ kind: 'connecting' })
    setAttempt((n) => n + 1)
  }

  const canReconnect = !session.request.oneOff

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} terminal`}>
      <SessionStatusBar phase={phase}>
        {telnet && phase.kind === 'connected' ? (
          <span
            className={styles.notice}
            title="Telnet doesn't encrypt its traffic, passwords included. Your browser's connection to NKeeper is protected; the hop from NKeeper to the host is not."
          >
            Unencrypted to host
          </span>
        ) : null}
        {phase.kind === 'ended' && canReconnect ? (
          <Button variant="secondary" size="sm" onClick={reconnect}>
            Reconnect
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={styles.terminalArea}>
        <div ref={containerRef} className={styles.terminal} />
        {phase.kind === 'connecting' && !phase.target ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decideHostKey} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard
            message={phase.message}
            code={phase.code}
            canReconnect={canReconnect}
            onReconnect={reconnect}
            onShowHost={() => onShowHost(session.hostId)}
          />
        ) : null}
      </div>
    </div>
  )
}
