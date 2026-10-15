import { FitAddon } from '@xterm/addon-fit'
import { Terminal } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'
import { useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { apiWebSocketUrl } from '../../../lib/api'
import type { ServerMessage, TerminalSession } from '../types'
import styles from './SshTerminal.module.css'

interface SshTerminalProps {
  session: TerminalSession
  /** Only the active tab is visible; it gets keyboard focus and is resized to fit. */
  active: boolean
  /** Show the host's details (e.g. to forget a changed host key). */
  onShowHost: (hostId: string) => void
}

type HostKeyPrompt = Extract<ServerMessage, { type: 'hostKey' }>

type Phase =
  | { kind: 'connecting'; target?: string }
  | { kind: 'hostKey'; prompt: HostKeyPrompt; target?: string }
  | { kind: 'connected'; target: string }
  | { kind: 'ended'; message: string; error: boolean; code?: string; target?: string }

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

/** One interactive SSH session: an xterm.js terminal bridged to the server over a WebSocket. */
export function SshTerminal({ session, active, onShowHost }: SshTerminalProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const terminalRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const socketRef = useRef<WebSocket | null>(null)
  const connectedRef = useRef(false)
  // One-off passwords are sent once and then forgotten.
  const requestRef = useRef(session.request)
  const [phase, setPhase] = useState<Phase>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)

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
    const socket = new WebSocket(apiWebSocketUrl('/api/ssh/session'))
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
          setPhase((current) => ({ kind: 'ended', message: message.message, error: true, code: message.code, target: current.target }))
          break
        case 'closed':
          finished = true
          setPhase((current) => ({ kind: 'ended', message: message.message, error: false, target: current.target }))
          break
      }
    }

    socket.onclose = () => {
      connectedRef.current = false
      if (!finished) {
        setPhase((current) => ({
          kind: 'ended',
          message: current.kind === 'connected' ? 'The connection was lost.' : 'Couldn’t reach the NKeeper server.',
          error: true,
          target: current.target,
        }))
      }
    }

    return () => {
      socket.onclose = null
      socket.onmessage = null
      socket.close()
    }
  }, [attempt])

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
  const target = phase.target

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} terminal`}>
      <div className={styles.statusBar} role="status">
        <span className={`${styles.dot} ${styles[phase.kind]} ${phase.kind === 'ended' && phase.error ? styles.failed : ''}`} />
        <span className={styles.statusText}>
          {phase.kind === 'connected'
            ? 'Connected'
            : phase.kind === 'ended'
              ? phase.error
                ? 'Not connected'
                : 'Session ended'
              : phase.kind === 'hostKey'
                ? 'Waiting for host key confirmation'
                : 'Connecting…'}
        </span>
        {target ? <span className={styles.target}>{target}</span> : null}
        {phase.kind === 'ended' && canReconnect ? (
          <Button variant="secondary" size="sm" className={styles.statusAction} onClick={reconnect}>
            Reconnect
          </Button>
        ) : null}
      </div>

      <div className={styles.terminalArea}>
        <div ref={containerRef} className={styles.terminal} />

        {phase.kind === 'connecting' && !target ? (
          <div className={styles.overlay}>
            <div className={styles.card}>
              <Spinner /> Opening session…
            </div>
          </div>
        ) : null}

        {phase.kind === 'hostKey' ? <HostKeyCard prompt={phase.prompt} target={target} onDecide={decideHostKey} /> : null}

        {phase.kind === 'ended' && phase.error ? (
          <div className={styles.overlay}>
            <div className={`${styles.card} ${styles.errorCard}`} role="alert">
              <h3 className={styles.cardTitle}>{phase.code === 'host_key_changed' ? 'Host key changed' : 'Couldn’t connect'}</h3>
              <p>{phase.message}</p>
              <div className={styles.cardActions}>
                {phase.code === 'host_key_changed' ? (
                  <Button variant="secondary" size="sm" onClick={() => onShowHost(session.hostId)}>
                    Show host details
                  </Button>
                ) : null}
                {canReconnect ? (
                  <Button size="sm" onClick={reconnect}>
                    Try again
                  </Button>
                ) : (
                  <p className={styles.hint}>Open a new session from the host’s menu to sign in again.</p>
                )}
              </div>
            </div>
          </div>
        ) : null}
      </div>
    </div>
  )
}

function HostKeyCard({ prompt, target, onDecide }: { prompt: HostKeyPrompt; target?: string; onDecide: (trust: boolean) => void }) {
  if (prompt.status === 'changed') {
    // The server refuses the connection; an error message follows.
    return null
  }
  return (
    <div className={styles.overlay}>
      <div className={styles.card} role="alertdialog" aria-labelledby="host-key-title" aria-describedby="host-key-text">
        <h3 id="host-key-title" className={styles.cardTitle}>
          Trust this host key?
        </h3>
        <p id="host-key-text">
          This is the first connection to {target ?? `port ${prompt.port}`}. Check that the fingerprint matches the server’s before
          trusting it; NKeeper will refuse the connection if it ever changes.
        </p>
        <dl className={styles.fingerprint}>
          <dt>{prompt.algorithm}</dt>
          <dd>SHA256:{prompt.fingerprint}</dd>
        </dl>
        <div className={styles.cardActions}>
          <Button variant="secondary" size="sm" onClick={() => onDecide(false)}>
            Cancel
          </Button>
          <Button size="sm" onClick={() => onDecide(true)} autoFocus>
            Trust and connect
          </Button>
        </div>
      </div>
    </div>
  )
}
