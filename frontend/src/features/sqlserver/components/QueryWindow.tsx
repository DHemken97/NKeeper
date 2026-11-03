import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { ApiError } from '../../../lib/api'
import { useCloseOnPageExit } from '../../../lib/pageExit'
import type { SessionRequest } from '../../terminal/types'
import { errorHeading, formatElapsed, lineRange, outputText, scriptToRun } from '../format'
import { sqlServerApi, type SqlQueryResult } from '../sqlServerApi'
import { useKeepAlive } from '../useKeepAlive'
import { ResultGrid } from './ResultGrid'
import { storedNumber, storeNumber } from '../storedSize'
import { Splitter } from './Splitter'
import styles from './SqlServer.module.css'

interface QueryWindowProps {
  title: string
  request: SessionRequest
  /** e.g. "sa@10.0.0.5:1433", for the status line. */
  target: string
  databases: string[]
  initialSql: string
  initialDatabase?: string
  /** Runs the script as soon as the window is connected (Select Top 1000 Rows). */
  runOnOpen?: boolean
  active: boolean
}

const EDITOR_SHARE_KEY = 'nkeeper.sql.editorShare'
/** The editor's share of the height it splits with the results, as in SSMS's default. */
const DEFAULT_EDITOR_SHARE = 0.45
const MIN_SHARE = 0.1
const MAX_SHARE = 0.9

const clampShare = (share: number) => Math.min(Math.max(share, MIN_SHARE), MAX_SHARE)

type Connection =
  | { state: 'opening' }
  | { state: 'ready'; id: string; spid: number; login: string }
  | { state: 'ended'; message: string }

/**
 * A query window, as in SSMS: its own connection (its own SPID, current database, temp tables and
 * transactions), an editor, and the results as grids or text with the messages beside them. F5,
 * Ctrl+Enter or Ctrl+E run the selection, or everything when nothing is selected.
 */
export function QueryWindow({ title, request, target, databases, initialSql, initialDatabase, runOnOpen, active }: QueryWindowProps) {
  const [connection, setConnection] = useState<Connection>({ state: 'opening' })
  const [attempt, setAttempt] = useState(0)
  const [sql, setSql] = useState(initialSql)
  const [database, setDatabase] = useState(initialDatabase ?? '')
  const [running, setRunning] = useState(false)
  const [result, setResult] = useState<SqlQueryResult | null>(null)
  const [runError, setRunError] = useState<string | null>(null)
  const [finishedAt, setFinishedAt] = useState<Date | null>(null)
  const [view, setView] = useState<'grid' | 'text'>('grid')
  const [tab, setTab] = useState<'results' | 'messages'>('results')
  const editorRef = useRef<HTMLTextAreaElement>(null)
  const gutterRef = useRef<HTMLDivElement>(null)
  const ranOnOpen = useRef(false)
  const editorPaneRef = useRef<HTMLDivElement>(null)
  const resultsPaneRef = useRef<HTMLDivElement>(null)
  // Shared by every window in this browser: the last split you chose.
  const [editorShare, setEditorShare] = useState(() => clampShare(storedNumber(EDITOR_SHARE_KEY, DEFAULT_EDITOR_SHARE)))

  function resizeEditor(share: number) {
    const next = clampShare(share)
    setEditorShare(next)
    storeNumber(EDITOR_SHARE_KEY, next)
  }

  /** The pointer's height as a share of the editor and results together. */
  function shareAt(clientY: number): number {
    const top = editorPaneRef.current?.getBoundingClientRect().top ?? 0
    const bottom = resultsPaneRef.current?.getBoundingClientRect().bottom ?? top + 1
    return (clientY - top) / Math.max(bottom - top, 1)
  }

  useEffect(() => {
    let cancelled = false
    let opened: string | null = null
    sqlServerApi.open(request, undefined, initialDatabase).then(
      (response) => {
        if (cancelled) {
          if (response.sessionId) {
            void sqlServerApi.close(response.sessionId).catch(() => {})
          }
          return
        }
        if (response.status !== 'ready' || !response.sessionId || !response.server) {
          setConnection({ state: 'ended', message: 'The server’s certificate needs to be trusted again. Reconnect the SQL Server tab.' })
          return
        }
        opened = response.sessionId
        setConnection({ state: 'ready', id: response.sessionId, spid: response.server.sessionId, login: response.server.login })
        setDatabase(response.database ?? '')
      },
      (error: unknown) => !cancelled && setConnection({ state: 'ended', message: error instanceof ApiError ? error.message : 'Couldn’t open a connection for this window.' }),
    )
    return () => {
      cancelled = true
      if (opened) {
        void sqlServerApi.close(opened).catch(() => {})
      }
    }
    // The request and starting database are fixed for the window's life; `attempt` reconnects.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [attempt])

  const sessionId = connection.state === 'ready' ? connection.id : null
  useCloseOnPageExit(sessionId ? `/api/mssql/sessions/${sessionId}` : null)
  useKeepAlive(sessionId, () => setConnection({ state: 'ended', message: 'This window’s connection has ended.' }))

  useEffect(() => {
    if (sessionId && runOnOpen && !ranOnOpen.current) {
      ranOnOpen.current = true
      void execute()
    }
    // Only once, when the window first connects.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId])

  useEffect(() => {
    if (active) {
      editorRef.current?.focus()
    }
  }, [active, sessionId])

  async function execute() {
    if (!sessionId || running) {
      return
    }
    const editor = editorRef.current
    const { sql: script, lineOffset } = scriptToRun(sql, editor?.selectionStart ?? 0, editor?.selectionEnd ?? 0)
    if (!script.trim()) {
      return
    }
    setRunning(true)
    setRunError(null)
    try {
      const next = await sqlServerApi.query(sessionId, script, lineOffset)
      setResult(next)
      setFinishedAt(new Date())
      setTab(next.output.some((o) => o.kind === 'results') ? 'results' : 'messages')
      if (next.database) {
        setDatabase(next.database)
      }
      if (next.connectionLost) {
        setConnection({ state: 'ended', message: 'The connection was lost (the server ended it, or a severe error closed it).' })
      }
    } catch (error) {
      if (error instanceof ApiError && error.code === 'session_not_found') {
        setConnection({ state: 'ended', message: error.message })
      } else {
        setRunError(error instanceof ApiError ? error.message : 'Couldn’t run the query.')
      }
    } finally {
      setRunning(false)
    }
  }

  function cancel() {
    if (sessionId) {
      void sqlServerApi.cancel(sessionId).catch(() => {})
    }
  }

  async function changeDatabase(name: string) {
    if (!sessionId) {
      return
    }
    setRunError(null)
    try {
      setDatabase((await sqlServerApi.changeDatabase(sessionId, name)).name)
    } catch (error) {
      setRunError(error instanceof ApiError ? error.message : 'Couldn’t change the database.')
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    const ctrl = event.ctrlKey || event.metaKey
    if (event.key === 'F5' || (ctrl && event.key === 'Enter') || (ctrl && !event.shiftKey && event.key.toLowerCase() === 'e')) {
      event.preventDefault()
      void execute()
    } else if (event.key === 'Tab' && !ctrl && !event.altKey && !event.shiftKey) {
      // Indent, as editors do; Shift+Tab still leaves the editor.
      event.preventDefault()
      const editor = event.currentTarget
      const { selectionStart: start, selectionEnd: end } = editor
      const next = `${sql.slice(0, start)}    ${sql.slice(end)}`
      setSql(next)
      requestAnimationFrame(() => editor.setSelectionRange(start + 4, start + 4))
    }
  }

  /** Selects an error's line in the editor (as double-clicking it does in SSMS). */
  function goToLine(line: number) {
    const editor = editorRef.current
    if (!editor) {
      return
    }
    const [start, end] = lineRange(sql, line)
    editor.focus()
    editor.setSelectionRange(start, end)
  }

  const lines = sql.split('\n').length
  const output = result?.output ?? []
  const resultSets = output.filter((o) => o.kind === 'results')
  const messages = output.filter((o) => o.kind !== 'results')
  const rowsSent = resultSets.reduce((sum, r) => sum + (r.rowCount ?? 0), 0)
  const databaseOptions = database && !databases.includes(database) ? [database, ...databases] : databases

  return (
    <div className={styles.queryWindow} hidden={!active} role="tabpanel" aria-label={title}>
      <div className={styles.toolbar}>
        {running ? (
          <Button size="sm" variant="danger" onClick={cancel}>
            Cancel
          </Button>
        ) : (
          <Button size="sm" onClick={() => void execute()} disabled={!sessionId} title="F5 or Ctrl+Enter">
            ▶ Execute
          </Button>
        )}
        <label className={styles.toolbarField}>
          <span>Database</span>
          <select value={database} onChange={(event) => void changeDatabase(event.target.value)} disabled={!sessionId || running} aria-label="Database">
            {databaseOptions.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </label>
        <div className={styles.segmented} role="group" aria-label="Results to">
          <button type="button" aria-pressed={view === 'grid'} onClick={() => setView('grid')}>
            Grid
          </button>
          <button type="button" aria-pressed={view === 'text'} onClick={() => setView('text')}>
            Text
          </button>
        </div>
        {connection.state === 'ready' ? (
          <span className={styles.toolbarNote}>
            {connection.login} ({connection.spid})
          </span>
        ) : null}
      </div>

      {connection.state === 'ended' ? (
        <div className={styles.windowNotice} role="alert">
          <span>{connection.message}</span>
          <Button size="sm" variant="secondary" onClick={() => {
            setConnection({ state: 'opening' })
            setAttempt((n) => n + 1)
          }}>
            Reconnect
          </Button>
        </div>
      ) : null}

      <div className={styles.editor} ref={editorPaneRef} style={{ flexGrow: editorShare }}>
        <div className={styles.gutter} ref={gutterRef} aria-hidden="true">
          {Array.from({ length: lines }, (_, i) => (
            <div key={i}>{i + 1}</div>
          ))}
        </div>
        <textarea
          ref={editorRef}
          className={styles.code}
          value={sql}
          onChange={(event) => setSql(event.target.value)}
          onKeyDown={onKeyDown}
          onScroll={(event) => {
            if (gutterRef.current) {
              gutterRef.current.scrollTop = event.currentTarget.scrollTop
            }
          }}
          spellCheck={false}
          autoCapitalize="off"
          autoComplete="off"
          wrap="off"
          aria-label={`${title} query`}
          placeholder="SELECT TOP (100) * FROM sys.objects;  — F5 or Ctrl+Enter runs it (or just the selection)"
        />
      </div>

      <Splitter
        direction="rows"
        label="Resize editor and results"
        value={editorShare * 100}
        min={MIN_SHARE * 100}
        max={MAX_SHARE * 100}
        onMove={(y) => resizeEditor(shareAt(y))}
        onStep={(direction, big) => resizeEditor(editorShare + direction * (big ? 0.15 : 0.05))}
        onReset={() => resizeEditor(DEFAULT_EDITOR_SHARE)}
      />

      <div className={styles.results} aria-label="Results" ref={resultsPaneRef} style={{ flexGrow: 1 - editorShare }}>
        {result || runError || running ? (
          view === 'grid' ? (
            <>
              <div className={styles.resultTabs} role="tablist" aria-label="Results and messages">
                {resultSets.length > 0 ? (
                  <button type="button" role="tab" aria-selected={tab === 'results'} onClick={() => setTab('results')}>
                    Results
                  </button>
                ) : null}
                <button type="button" role="tab" aria-selected={tab === 'messages' || resultSets.length === 0} onClick={() => setTab('messages')}>
                  Messages
                </button>
              </div>
              <div className={styles.resultBody}>
                {running ? (
                  <p className={styles.running}>
                    <Spinner size="sm" /> Executing query…
                  </p>
                ) : null}
                {runError ? (
                  <p className={styles.errorText} role="alert">
                    {runError}
                  </p>
                ) : null}
                {!running && tab === 'results' && resultSets.length > 0
                  ? resultSets.map((set, i) => <ResultGrid key={i} output={set} label={`Result ${i + 1}`} />)
                  : null}
                {!running && (tab === 'messages' || resultSets.length === 0) && result ? (
                  <div className={styles.messages} role="log" aria-label="Messages">
                    {messages.map((message, i) =>
                      message.kind === 'error' ? (
                        <button
                          key={i}
                          type="button"
                          className={styles.errorMessage}
                          onDoubleClick={() => message.line && goToLine(message.line)}
                          onClick={(event) => event.detail === 0 && message.line && goToLine(message.line)}
                          title={message.line ? 'Double-click to go to the line' : undefined}
                        >
                          {errorHeading(message) ? <span>{errorHeading(message)}</span> : null}
                          <span>{message.text}</span>
                        </button>
                      ) : (
                        <p key={i}>{message.text}</p>
                      ),
                    )}
                    {finishedAt ? <p className={styles.completion}>Completion time: {finishedAt.toISOString()}</p> : null}
                  </div>
                ) : null}
              </div>
            </>
          ) : (
            <div className={styles.resultBody}>
              {running ? (
                <p className={styles.running}>
                  <Spinner size="sm" /> Executing query…
                </p>
              ) : null}
              {runError ? (
                <p className={styles.errorText} role="alert">
                  {runError}
                </p>
              ) : null}
              {!running && result ? (
                <pre className={styles.textResults} aria-label="Results as text">
                  {outputText(output)}
                  {finishedAt ? `\n\nCompletion time: ${finishedAt.toISOString()}` : ''}
                </pre>
              ) : null}
            </div>
          )
        ) : (
          <p className={styles.resultsEmpty}>Results appear here.</p>
        )}
      </div>

      <div
        className={`${styles.queryStatus} ${result?.cancelled || result?.hasErrors ? styles.queryStatusWarn : ''}`}
        role="status"
        aria-label={`${title} status`}
      >
        <span>
          {connection.state === 'opening'
            ? 'Connecting…'
            : running
              ? 'Executing query…'
              : !result
                ? connection.state === 'ready'
                  ? 'Connected.'
                  : 'Disconnected.'
                : result.cancelled
                  ? 'Query was cancelled.'
                  : result.hasErrors
                    ? 'Query completed with errors.'
                    : 'Query executed successfully.'}
        </span>
        <span>{target}</span>
        {database ? <span>{database}</span> : null}
        {result ? <span>{formatElapsed(result.elapsedMs)}</span> : null}
        {result ? <span>{rowsSent.toLocaleString()} rows</span> : null}
      </div>
    </div>
  )
}
