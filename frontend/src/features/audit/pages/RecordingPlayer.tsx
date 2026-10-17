import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import type { Terminal } from '@xterm/xterm'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { auditApi, parseCast, type Cast, type Recording } from '../auditApi'
import reportStyles from '../../reports/pages/Reports.module.css'
import styles from './Audit.module.css'

/** Pauses longer than this play as this long, so a recording isn't mostly waiting. */
const MAX_IDLE_SECONDS = 2
const SPEEDS = [1, 2, 4, 8]
const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' })

function clock(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

/** Plays back a recorded SSH or Telnet session in a terminal, at its original pace (or faster). */
export function RecordingPlayer() {
  const { id } = useParams()
  const containerRef = useRef<HTMLDivElement>(null)
  const terminalRef = useRef<Terminal | null>(null)
  const [recording, setRecording] = useState<Recording | null>(null)
  const [cast, setCast] = useState<Cast | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [playing, setPlaying] = useState(false)
  const [speed, setSpeed] = useState(1)
  const [position, setPosition] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([auditApi.recording(id!, controller.signal), auditApi.cast(id!, controller.signal)])
      .then(([meta, text]) => {
        setRecording(meta)
        setCast(parseCast(text))
      })
      .catch((err: unknown) => {
        if (!(err instanceof DOMException && err.name === 'AbortError')) {
          setError(err)
        }
      })
    return () => controller.abort()
  }, [id])

  // The terminal, sized as the session was, created once the recording is loaded.
  useEffect(() => {
    if (!cast || !containerRef.current) {
      return
    }
    let disposed = false
    void Promise.all([import('@xterm/xterm'), import('@xterm/xterm/css/xterm.css')]).then(([{ Terminal: XTerm }]) => {
      if (disposed || !containerRef.current) {
        return
      }
      const terminal = new XTerm({
        cols: cast.width,
        rows: cast.height,
        disableStdin: true,
        convertEol: false,
        fontFamily: "ui-monospace, 'Cascadia Mono', Consolas, 'DejaVu Sans Mono', monospace",
        fontSize: 14,
        theme: { background: '#0b1220', foreground: '#e5ebf5' },
      })
      terminal.open(containerRef.current)
      terminalRef.current = terminal
      setPlaying(true)
    })
    return () => {
      disposed = true
      terminalRef.current?.dispose()
      terminalRef.current = null
    }
  }, [cast])

  // Plays one event at a time, waiting (scaled by the speed) for the next.
  useEffect(() => {
    if (!playing || !cast || !terminalRef.current || position >= cast.events.length) {
      return
    }
    const [time, kind, data] = cast.events[position]
    const previous = position === 0 ? 0 : cast.events[position - 1][0]
    const wait = (Math.min(time - previous, MAX_IDLE_SECONDS) * 1000) / speed
    const timer = setTimeout(() => {
      const terminal = terminalRef.current
      if (kind === 'o') {
        terminal?.write(data)
      } else if (kind === 'r') {
        const [cols, rows] = data.split('x').map(Number)
        if (cols > 0 && rows > 0) {
          terminal?.resize(cols, rows)
        }
      }
      setPosition((p) => p + 1)
    }, wait)
    return () => clearTimeout(timer)
  }, [playing, cast, position, speed])

  function restart() {
    terminalRef.current?.reset()
    if (cast) {
      terminalRef.current?.resize(cast.width, cast.height)
    }
    setPosition(0)
    setPlaying(true)
  }

  const finished = cast !== null && position >= cast.events.length
  const total = cast?.events.at(-1)?.[0] ?? 0
  const elapsed = cast && position > 0 ? cast.events[Math.min(position, cast.events.length) - 1][0] : 0

  return (
    <>
      <header className={reportStyles.reportHeader}>
        <div>
          <h2 className={reportStyles.reportTitle}>
            {recording ? `${recording.protocol} session on ${recording.hostName ?? 'a deleted host'}` : 'Recording'}
          </h2>
          {recording ? (
            <p className={reportStyles.reportSummary}>
              {recording.userName ?? 'A deleted user'} · {timeFormat.format(new Date(recording.startedAtUtc))}
              {recording.truncated ? ' · The recording stopped at its size limit; the session went on.' : ''}
            </p>
          ) : null}
        </div>
        <div className={reportStyles.reportActions}>
          <Link to="../.." relative="path" className={styles.download}>
            Back to the audit log
          </Link>
        </div>
      </header>

      {error ? (
        <div className={reportStyles.state}>
          <Alert>{toFormErrors(error).message}</Alert>
        </div>
      ) : !cast ? (
        <div className={reportStyles.state}>
          <Spinner label="Loading the recording" />
        </div>
      ) : (
        <div className={styles.player}>
          <div className={styles.controls}>
            <Button size="sm" onClick={() => (finished ? restart() : setPlaying(!playing))}>
              {finished ? 'Replay' : playing ? 'Pause' : 'Play'}
            </Button>
            <Button size="sm" variant="secondary" onClick={restart}>
              Restart
            </Button>
            <label className={styles.date}>
              Speed
              <select className={styles.input} value={speed} onChange={(event) => setSpeed(Number(event.target.value))} aria-label="Playback speed">
                {SPEEDS.map((s) => (
                  <option key={s} value={s}>
                    {s}×
                  </option>
                ))}
              </select>
            </label>
            <span className={styles.clock} aria-label="Position">
              {clock(elapsed)} / {clock(total)}
            </span>
          </div>
          <div ref={containerRef} className={styles.screen} aria-label="Recorded terminal" />
        </div>
      )}
    </>
  )
}
