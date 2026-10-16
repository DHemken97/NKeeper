import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Button } from '../../../components/ui/Button'
import { apiWebSocketUrl } from '../../../lib/api'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar } from './SessionChrome'
import styles from './Session.module.css'

interface RpcSessionProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
}

type RpcMessage =
  | { type: 'status'; target: string }
  | { type: 'connected'; target: string }
  | { type: 'response'; stub: string }
  | { type: 'fault'; message: string; status?: number | null }
  | { type: 'error'; code: string; message: string }

interface Exchange {
  id: number
  text: string
}

/** One Windows RPC connection: bind (and sign in) happens on the server; the user sends raw calls as hex stubs. */
export function RpcSession({ session, active, onShowHost }: RpcSessionProps) {
  const socketRef = useRef<WebSocket | null>(null)
  const requestRef = useRef(session.request)
  const nextId = useRef(0)
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)
  const [opnum, setOpnum] = useState('0')
  const [stub, setStub] = useState('')
  const [log, setLog] = useState<Exchange[]>([])

  useEffect(() => {
    const request = requestRef.current
    const socket = new WebSocket(apiWebSocketUrl('/api/rpc/session'))
    socketRef.current = socket
    let failed = false

    const add = (text: string) => setLog((current) => [...current, { id: nextId.current++, text }])

    socket.onopen = () => {
      socket.send(
        JSON.stringify({
          type: 'start',
          hostId: request.hostId,
          serviceIndex: request.serviceIndex,
          credentialId: request.credentialId,
          username: request.oneOff?.username,
          password: request.oneOff?.password,
          domain: request.oneOff?.domain,
        }),
      )
      if (request.oneOff) {
        requestRef.current = { ...request, oneOff: undefined }
      }
    }

    socket.onmessage = (event: MessageEvent<string>) => {
      const message = JSON.parse(event.data) as RpcMessage
      switch (message.type) {
        case 'status':
          setPhase({ kind: 'connecting', target: message.target })
          break
        case 'connected':
          setPhase({ kind: 'connected', target: message.target })
          break
        case 'response':
          add(`← ${message.stub || '(empty)'}`)
          break
        case 'fault':
          add(`✗ ${message.message}${message.status != null ? ` (0x${message.status.toString(16).padStart(8, '0')})` : ''}`)
          break
        case 'error':
          failed = true
          setPhase((current) => ({ kind: 'ended', message: message.message, code: message.code, target: current.target }))
          break
      }
    }

    socket.onclose = () => {
      if (!failed) {
        failed = true
        setPhase((current) => ({ kind: 'ended', message: 'The connection was closed.', target: current.target }))
      }
    }

    return () => {
      socket.onclose = null
      socket.onmessage = null
      socket.close()
    }
  }, [attempt])

  function send(event: FormEvent) {
    event.preventDefault()
    const socket = socketRef.current
    const number = Number(opnum)
    if (!socket || socket.readyState !== WebSocket.OPEN || !Number.isInteger(number) || number < 0 || number > 65535) {
      return
    }
    const hex = stub.replace(/\s+/g, '')
    setLog((current) => [...current, { id: nextId.current++, text: `→ opnum ${number}${hex ? ` ${hex}` : ''}` }])
    socket.send(JSON.stringify({ type: 'call', opnum: number, stub: hex }))
  }

  function reconnect() {
    setPhase({ kind: 'connecting' })
    setLog([])
    setAttempt((n) => n + 1)
  }

  const canReconnect = !session.request.oneOff

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} RPC`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'ended' && canReconnect ? (
          <Button variant="secondary" size="sm" onClick={reconnect}>
            Reconnect
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={styles.terminalArea} style={{ padding: 12, overflow: 'auto' }}>
        {phase.kind === 'connected' ? (
          <>
            <form onSubmit={send} style={{ display: 'flex', gap: 8, marginBottom: 12 }}>
              <input aria-label="Operation number" type="number" min={0} max={65535} value={opnum} onChange={(e) => setOpnum(e.target.value)} style={{ width: 90 }} />
              <input aria-label="Stub (hex)" value={stub} onChange={(e) => setStub(e.target.value)} placeholder="Stub bytes in hex (optional)" style={{ flex: 1 }} />
              <Button type="submit" size="sm">
                Call
              </Button>
            </form>
            <pre aria-live="polite" style={{ margin: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
              {log.map((entry) => entry.text).join('\n')}
            </pre>
          </>
        ) : null}
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect={canReconnect} onReconnect={reconnect} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
      </div>
    </div>
  )
}
