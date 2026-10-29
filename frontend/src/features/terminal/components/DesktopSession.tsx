import { useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { apiWebSocketUrl } from '../../../lib/api'
import Guacamole from '../../../vendor/guacamole-common-js/guacamole.js'
import { describeDesktopStatus } from '../desktopStatus'
import { toggleFullscreen } from '../fullscreen'
import type { RemoteSession, ServerMessage, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface DesktopSessionProps {
  session: RemoteSession
  /** Only the active tab is visible and receives the keyboard. */
  active: boolean
  onShowHost: (hostId: string) => void
  /** The desktop session ended normally (e.g. sign out); the tab closes itself. */
  onEnded: () => void
}

type GuacamoleClient = InstanceType<typeof Guacamole.Client>
type GuacamoleStatus = InstanceType<typeof Guacamole.Status>

// X11 keysyms for Ctrl+Alt+Del.
const KEYSYM_CONTROL_LEFT = 0xffe3
const KEYSYM_ALT_LEFT = 0xffe9
const KEYSYM_DELETE = 0xffff
const RESIZE_DEBOUNCE_MS = 250

// @types/guacamole-common-js describes 1.5; these 1.6 runtime APIs are missing or marked abstract there.
const TunnelBase = Guacamole.Tunnel as unknown as new () => InstanceType<typeof Guacamole.Tunnel> & { setState: (state: number) => void }
const toInstruction = (elements: unknown[]) =>
  (Guacamole.Parser as unknown as { toInstruction: (elements: unknown[]) => string }).toInstruction(elements)

/** Remote resolution in physical pixels for a viewport, so text is crisp on high-DPI screens. */
function desktopSize(element: HTMLElement) {
  const ratio = window.devicePixelRatio || 1
  const clamp = (value: number) => Math.min(8192, Math.max(200, Math.round(value)))
  return {
    width: clamp(element.clientWidth * ratio),
    height: clamp(element.clientHeight * ratio),
    dpi: Math.min(480, Math.max(48, Math.round(96 * ratio))),
  }
}


/**
 * One remote desktop session (RDP or VNC): the browser half of the Apache Guacamole client, fed by NKeeper's
 * WebSocket instead of Guacamole's own tunnel. Keyboard input goes to the remote desktop only
 * while the desktop has focus; the text clipboard syncs both ways.
 */
export function DesktopSession({ session, active, onShowHost, onEnded }: DesktopSessionProps) {
  const protocol = session.protocol === 'vnc' ? 'vnc' : 'rdp'
  const areaRef = useRef<HTMLDivElement>(null)
  const viewportRef = useRef<HTMLDivElement>(null)
  const clientRef = useRef<GuacamoleClient | null>(null)
  const socketRef = useRef<WebSocket | null>(null)
  const keyboardRef = useRef<InstanceType<typeof Guacamole.Keyboard> | null>(null)
  const lastClipboardRef = useRef<string | null>(null)
  const requestRef = useRef(session.request)
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)
  const onEndedRef = useRef(onEnded)
  useEffect(() => {
    onEndedRef.current = onEnded
  })

  useEffect(() => {
    const viewport = viewportRef.current!
    const request = requestRef.current
    const socket = new WebSocket(apiWebSocketUrl(`/api/${protocol}/session`))
    socketRef.current = socket
    let parser: InstanceType<typeof Guacamole.Parser> | null = null
    let client: GuacamoleClient | null = null
    let failed = false
    let finished = false

    const fail = (message: string, code?: string) => {
      failed = true
      setPhase((current) => ({ kind: 'ended', message, code, target: current.target }))
    }

    socket.onopen = () => {
      const size = desktopSize(viewport)
      socket.send(
        JSON.stringify({
          type: 'start',
          hostId: request.hostId,
          serviceIndex: request.serviceIndex,
          credentialId: request.credentialId,
          useSignInCredentials: request.useSignIn,
          username: request.oneOff?.username,
          password: request.oneOff?.password,
          domain: request.oneOff?.domain,
          ...size,
          audio: Guacamole.AudioPlayer.getSupportedTypes(),
          image: ['image/png', 'image/jpeg', 'image/webp'],
          timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
        }),
      )
      if (request.oneOff) {
        requestRef.current = { ...request, oneOff: undefined }
      }
    }

    /** After "connected", every frame is Guacamole protocol for the client. */
    const startClient = () => {
      const tunnel = new TunnelBase()
      tunnel.connect = () => tunnel.setState(Guacamole.Tunnel.State.OPEN)
      tunnel.disconnect = () => tunnel.setState(Guacamole.Tunnel.State.CLOSED)
      tunnel.sendMessage = (...elements: unknown[]) => {
        if (socket.readyState === WebSocket.OPEN) {
          socket.send(toInstruction(elements))
        }
      }

      parser = new Guacamole.Parser()
      parser.oninstruction = (opcode: string, args: unknown[]) => {
        // NKeeper's own marker: guacd ended the session.
        if (opcode === '' && args[0] === 'closed') {
          finished = true
          if (!failed) {
            onEndedRef.current()
          }
          return
        }
        tunnel.oninstruction?.(opcode, args)
      }

      client = new Guacamole.Client(tunnel)
      clientRef.current = client
      const display = client.getDisplay()
      const element = display.getElement()
      element.classList.add(styles.display)
      viewport.replaceChildren(element)

      // Fit the remote desktop (sized in physical pixels) into the viewport.
      display.onresize = () => {
        const scale = Math.min(viewport.clientWidth / display.getWidth(), viewport.clientHeight / display.getHeight())
        if (Number.isFinite(scale) && scale > 0) {
          display.scale(scale)
        }
      }

      client.onerror = (status: GuacamoleStatus) => {
        // Status 0 is a clean end (e.g. the user signed out).
        if (status.code !== 0) {
          fail(describeDesktopStatus(status, protocol))
        }
      }

      // Remote → local clipboard (text only; the server never accepts other types).
      client.onclipboard = (stream, mimetype) => {
        if (mimetype !== 'text/plain') {
          return
        }
        const reader = new Guacamole.StringReader(stream)
        let text = ''
        reader.ontext = (chunk: string) => {
          text += chunk
        }
        reader.onend = () => {
          lastClipboardRef.current = text
          void navigator.clipboard?.writeText(text).catch(() => {})
        }
      }

      const mouse = new Guacamole.Mouse(element)
      mouse.onEach(['mousedown', 'mouseup', 'mousemove'], (event) => {
        client?.sendMouseState((event as unknown as { state: InstanceType<typeof Guacamole.Mouse.State> }).state, true)
      })

      const keyboard = new Guacamole.Keyboard(viewport)
      keyboard.onkeydown = (keysym: number) => {
        client?.sendKeyEvent(1, keysym)
        return false
      }
      keyboard.onkeyup = (keysym: number) => client?.sendKeyEvent(0, keysym)
      keyboardRef.current = keyboard

      client.connect()
      viewport.focus()
    }

    socket.onmessage = (event: MessageEvent<string>) => {
      if (parser) {
        parser.receive(event.data)
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
          setPhase({ kind: 'connected', target: message.target })
          startClient()
          break
        case 'error':
          fail(message.message, message.code)
          break
      }
    }

    socket.onclose = () => {
      if (!finished && !failed) {
        fail(client ? 'The connection was lost.' : 'Couldn’t reach the NKeeper server.')
      }
    }

    // Keep the remote resolution matched to the viewport.
    let resizeTimer: number | undefined
    const observer = new ResizeObserver(() => {
      window.clearTimeout(resizeTimer)
      resizeTimer = window.setTimeout(() => {
        if (client && viewport.clientWidth > 0 && viewport.clientHeight > 0) {
          const { width, height } = desktopSize(viewport)
          client.sendSize(width, height)
          client.getDisplay().onresize?.(client.getDisplay().getWidth(), client.getDisplay().getHeight())
        }
      }, RESIZE_DEBOUNCE_MS)
    })
    observer.observe(viewport)

    return () => {
      observer.disconnect()
      window.clearTimeout(resizeTimer)
      socket.onclose = null
      socket.onmessage = null
      keyboardRef.current?.reset()
      keyboardRef.current = null
      // Ends the RDP session cleanly (sends "disconnect") rather than just dropping it.
      client?.disconnect()
      clientRef.current = null
      viewport.replaceChildren()
      socket.close()
    }
  }, [attempt, protocol])

  // The active tab gets the keyboard.
  useEffect(() => {
    if (active && clientRef.current) {
      viewportRef.current?.focus()
    }
  }, [active])

  /** Local → remote clipboard, when the desktop gains focus (the browser may ask for permission). */
  function syncClipboardToRemote() {
    const client = clientRef.current
    if (!client || !navigator.clipboard?.readText) {
      return
    }
    navigator.clipboard.readText().then(
      (text) => {
        if (text && text !== lastClipboardRef.current) {
          lastClipboardRef.current = text
          const writer = new Guacamole.StringWriter(client.createClipboardStream('text/plain'))
          writer.sendText(text)
          writer.sendEnd()
        }
      },
      () => {
        // Permission denied or unavailable (non-HTTPS origin): skip.
      },
    )
  }

  function decide(trust: boolean) {
    socketRef.current?.send(JSON.stringify({ type: 'hostKeyDecision', trust }))
    setPhase((current) => ({ kind: 'connecting', target: current.target }))
  }

  function sendCtrlAltDel() {
    const client = clientRef.current
    if (!client) {
      return
    }
    for (const keysym of [KEYSYM_CONTROL_LEFT, KEYSYM_ALT_LEFT, KEYSYM_DELETE]) {
      client.sendKeyEvent(1, keysym)
    }
    for (const keysym of [KEYSYM_DELETE, KEYSYM_ALT_LEFT, KEYSYM_CONTROL_LEFT]) {
      client.sendKeyEvent(0, keysym)
    }
    viewportRef.current?.focus()
  }

  async function showFullscreen() {
    // Chromium can then capture keys like Win, Alt+Tab and Esc for the remote desktop.
    await toggleFullscreen(areaRef.current)
    viewportRef.current?.focus()
  }

  function reconnect() {
    setPhase({ kind: 'connecting' })
    setAttempt((n) => n + 1)
  }

  const canReconnect = !session.request.oneOff

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} remote desktop`}>
      <SessionStatusBar phase={phase}>
        {protocol === 'vnc' && phase.kind === 'connected' ? (
          <span className={styles.notice} title="VNC doesn't encrypt its traffic. Your browser's connection to NKeeper is protected; the hop from NKeeper to the host is not.">
            Unencrypted to host
          </span>
        ) : null}
        {phase.kind === 'connected' ? (
          <>
            <Button variant="ghost" size="sm" onClick={sendCtrlAltDel}>
              Ctrl+Alt+Del
            </Button>
            <Button variant="ghost" size="sm" onClick={() => void showFullscreen()}>
              Fullscreen
            </Button>
          </>
        ) : null}
        {phase.kind === 'ended' && canReconnect ? (
          <Button variant="secondary" size="sm" onClick={reconnect}>
            Reconnect
          </Button>
        ) : null}
      </SessionStatusBar>

      <div ref={areaRef} className={styles.terminalArea}>
        <div
          ref={viewportRef}
          className={styles.desktop}
          tabIndex={0}
          aria-label="Remote desktop. Keyboard input goes to the remote computer while this has focus."
          onMouseDown={() => viewportRef.current?.focus()}
          onFocus={syncClipboardToRemote}
          onBlur={() => keyboardRef.current?.reset()}
        />
        {phase.kind === 'connecting' && !phase.target ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
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
