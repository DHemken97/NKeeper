import { useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { ApiError } from '../../../lib/api'
import { useCloseOnPageExit } from '../../../lib/pageExit'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from '../../terminal/components/SessionChrome'
import sessionStyles from '../../terminal/components/Session.module.css'
import type { HostKeyMessage, RemoteSession, SessionPhase } from '../../terminal/types'
import { DbEngineContext } from '../engineContext'
import { dbApi, DB_ENGINES, type DbEngine, type ToolKind, type SqlObjectNode, type SqlOpenResponse, type SqlScriptAction, type SqlServerInfo } from '../sqlServerApi'
import { useKeepAlive } from '../useKeepAlive'
import { BlockingManager } from './BlockingManager'
import { ObjectExplorer } from './ObjectExplorer'
import { OrphanedUsers } from './OrphanedUsers'
import { QueryWindow } from './QueryWindow'
import { ToolReportView } from './ToolReportView'
import { storedNumber, storeNumber } from '../storedSize'
import { Splitter } from './Splitter'
import styles from './SqlServer.module.css'

interface SqlServerViewProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
  /** SQL Server unless it says otherwise. */
  engine?: DbEngine
}

/** A document tab: a query window (its own connection, as in SSMS), or a Management tool. */
type SqlDocument =
  | { id: string; kind: 'query'; title: string; sql: string; database?: string; runOnOpen?: boolean }
  | { id: string; kind: ToolKind; title: string }

/** A document to open; it's titled SQLQueryN unless it says otherwise. */
type NewDocument = { kind: 'query'; sql: string; database?: string; runOnOpen?: boolean; title?: string } | { kind: ToolKind; title?: string }

const TOOL_TITLES: Record<ToolKind, string> = {
  blocking: 'Blocking Manager',
  orphans: 'Orphaned Users',
  replication: 'Replication Health',
  storage: 'Storage',
  backups: 'Backups',
  health: 'Health',
}

let nextDocument = 1

const EXPLORER_WIDTH_KEY = 'nkeeper.sql.explorerWidth'
const DEFAULT_EXPLORER_WIDTH = 280
const MIN_EXPLORER_WIDTH = 160
/** The query windows keep at least this much room beside the explorer. */
const MIN_DOCUMENTS_WIDTH = 320

/** The certificate prompt for a SQL Server (trust on first use). */
function certificatePrompt(response: SqlOpenResponse): HostKeyMessage {
  const certificate = response.certificate!
  return {
    type: 'hostKey',
    status: 'unknown',
    kind: 'certificate',
    port: Number(/:(\d+)$/.exec(response.target)?.[1] ?? 1433),
    algorithm: 'TLS certificate',
    fingerprint: certificate.fingerprint,
    subject: certificate.subject,
    expires: certificate.expires,
  }
}

const SQL_CERTIFICATE_NOTE = {
  text: 'A SQL Server without a certificate of its own uses a self-signed one (“SSL_Self_Signed_Fallback”).',
  hint: 'NKeeper always encrypts the connection, and pins it to the certificate you trust here.',
}

/**
 * A SQL Server instance, like a small SSMS: the object explorer on the left (resizable), query windows
 * and the blocking manager (opened from Management) as tabs on the right. The explorer and blocking manager use this tab's own
 * session; each query window opens another with the same login.
 */
export function SqlServerView({ session, active, onShowHost, engine = 'mssql' }: SqlServerViewProps) {
  const db = dbApi(engine)
  const info = DB_ENGINES[engine]
  const product = info.product
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [renewed, setRenewed] = useState(false)
  const [main, setMain] = useState<{ id: string; server: SqlServerInfo; target: string } | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [documents, setDocuments] = useState<SqlDocument[]>([])
  const [activeDocument, setActiveDocument] = useState<string | null>(null)
  const [databases, setDatabases] = useState<string[]>([])
  const [explorerError, setExplorerError] = useState<string | null>(null)
  const trustRef = useRef<string | undefined>(undefined)
  const workspaceRef = useRef<HTMLDivElement>(null)
  const [explorerWidth, setExplorerWidth] = useState(() => storedNumber(EXPLORER_WIDTH_KEY, DEFAULT_EXPLORER_WIDTH))

  function maxExplorerWidth(): number {
    const total = workspaceRef.current?.getBoundingClientRect().width || 1200
    return Math.max(MIN_EXPLORER_WIDTH, total - MIN_DOCUMENTS_WIDTH)
  }

  function resizeExplorer(width: number) {
    const next = Math.min(Math.max(width, MIN_EXPLORER_WIDTH), maxExplorerWidth())
    setExplorerWidth(next)
    storeNumber(EXPLORER_WIDTH_KEY, next)
  }

  useEffect(() => {
    let cancelled = false
    let opened: string | null = null
    db.open(session.request, trustRef.current).then(
      (response) => {
        if (cancelled) {
          if (response.sessionId) {
            void db.close(response.sessionId).catch(() => {})
          }
          return
        }
        if (response.status === 'trustRequired' && response.certificate) {
          setRenewed(Boolean(response.renewed))
          setPhase({ kind: 'hostKey', prompt: certificatePrompt(response), target: response.target })
          return
        }
        opened = response.sessionId!
        setMain({ id: response.sessionId!, server: response.server!, target: response.target })
        setPhase({ kind: 'connected', target: response.target })
        setDocuments((current) => {
          if (current.length > 0) {
            return current
          }
          const first: SqlDocument = { id: `doc-${nextDocument++}`, kind: 'query', title: 'SQLQuery1', sql: '', database: response.database ?? undefined }
          setActiveDocument(first.id)
          return [first]
        })
      },
      (error: unknown) => {
        if (!cancelled) {
          setPhase({
            kind: 'ended',
            code: error instanceof ApiError ? error.code : undefined,
            message: error instanceof ApiError ? error.message : `Couldn’t connect to the ${product} server.`,
          })
        }
      },
    )
    return () => {
      cancelled = true
      if (opened) {
        void db.close(opened).catch(() => {})
      }
    }
  }, [db, product, session.request, attempt])

  useCloseOnPageExit(main ? db.sessionPath(main.id) : null)
  useKeepAlive(main?.id ?? null, () => setPhase({ kind: 'ended', message: `This ${product} session has ended. Connect again.` }), db)

  useEffect(() => {
    if (!main) {
      return
    }
    let cancelled = false
    db.databases(main.id).then(
      (names) => !cancelled && setDatabases(names),
      () => {},
    )
    return () => {
      cancelled = true
    }
  }, [db, main])

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      setPhase({ kind: 'ended', target: phase.target, code: 'host_key_rejected', message: 'The certificate was not trusted, so NKeeper didn’t connect.' })
      return
    }
    trustRef.current = phase.prompt.fingerprint
    reconnect()
  }

  function reconnect() {
    setMain(null)
    setPhase({ kind: 'connecting' })
    setAttempt((n) => n + 1)
  }

  function addDocument(document: NewDocument) {
    if (document.kind !== 'query') {
      // One of each tool: opening it again shows the one that's open.
      const existing = documents.find((d) => d.kind === document.kind)
      if (existing) {
        setActiveDocument(existing.id)
        return
      }
    }
    const queries = documents.filter((d) => d.kind === 'query').length
    const id = `doc-${nextDocument++}`
    const title = document.title ?? (document.kind === 'query' ? `SQLQuery${queries + 1}` : TOOL_TITLES[document.kind])
    setDocuments((current) => [...current, { ...document, id, title } as SqlDocument])
    setActiveDocument(id)
  }

  function closeDocument(id: string) {
    setDocuments((current) => {
      const index = current.findIndex((d) => d.id === id)
      const next = current.filter((d) => d.id !== id)
      if (activeDocument === id) {
        setActiveDocument(next[Math.min(index, next.length - 1)]?.id ?? null)
      }
      return next
    })
  }

  async function script(node: SqlObjectNode, action: SqlScriptAction) {
    if (!main) {
      return
    }
    setExplorerError(null)
    try {
      const { sql } = await db.script(main.id, node, action)
      addDocument({
        kind: 'query',
        sql,
        database: node.database ?? undefined,
        // "Select Top 1000 Rows" runs straight away, as in SSMS.
        runOnOpen: action === 'selectTop',
        title: action === 'selectTop' ? `Top 1000 · ${node.name ?? node.label}` : undefined,
      })
    } catch (error) {
      setExplorerError(error instanceof ApiError ? error.message : 'Couldn’t script that object.')
    }
  }

  const connected = phase.kind === 'connected' && main

  return (
    <DbEngineContext.Provider value={engine}>
    <div className={sessionStyles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} ${product}`}>
      <SessionStatusBar phase={phase}>
        {connected ? (
          <>
            <span className={styles.serverInfo}>
              {product} {main.server.productVersion}
              {main.server.edition ? ` · ${main.server.edition}` : ''}
            </span>
            <Button variant="ghost" size="sm" onClick={() => addDocument({ kind: 'query', sql: '' })}>
              New Query
            </Button>
          </>
        ) : null}
      </SessionStatusBar>

      <div className={`${sessionStyles.terminalArea} ${styles.area}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? (
          <TrustCard
            prompt={phase.prompt}
            target={phase.target}
            onDecide={decide}
            certificateNote={SQL_CERTIFICATE_NOTE}
            intro={
              renewed
                ? `${phase.target} has a new self-signed certificate. SQL Server makes a new one each time it starts when it has none of its own, so this is expected after a restart.`
                : undefined
            }
          />
        ) : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={reconnect} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}

        {connected ? (
          <div className={styles.workspace} ref={workspaceRef}>
            <aside className={styles.explorer} aria-label="Object Explorer" style={{ width: explorerWidth }}>
              <div className={styles.paneTitle}>Object Explorer</div>
              {explorerError ? (
                <p className={styles.paneError} role="alert">
                  {explorerError}
                </p>
              ) : null}
              <ObjectExplorer
                sessionId={main.id}
                server={main.server}
                onNewQuery={(database) => addDocument({ kind: 'query', sql: '', database })}
                onScript={(node, action) => void script(node, action)}
                onOpenTool={(name) => {
                  if (name in TOOL_TITLES) {
                    addDocument({ kind: name as ToolKind })
                  } else {
                    // The server offers a tool this page doesn't know: the page is older than the server.
                    setExplorerError(`This page can't open “${name}” yet: reload the page to get the latest NKeeper.`)
                  }
                }}
              />
            </aside>
            <Splitter
              direction="columns"
              label="Resize Object Explorer"
              value={explorerWidth}
              min={MIN_EXPLORER_WIDTH}
              max={maxExplorerWidth()}
              onMove={(x) => resizeExplorer(x - (workspaceRef.current?.getBoundingClientRect().left ?? 0))}
              onStep={(direction, big) => resizeExplorer(explorerWidth + direction * (big ? 80 : 16))}
              onReset={() => resizeExplorer(DEFAULT_EXPLORER_WIDTH)}
            />

            <section className={styles.documents}>
              <div className={styles.documentTabs} role="tablist" aria-label="Query windows">
                {documents.map((document) => (
                  <div key={document.id} className={`${styles.documentTab} ${document.id === activeDocument ? styles.documentTabActive : ''}`}>
                    <button
                      type="button"
                      role="tab"
                      aria-selected={document.id === activeDocument}
                      className={styles.documentTabButton}
                      onClick={() => setActiveDocument(document.id)}
                    >
                      {document.title}
                    </button>
                    <button type="button" className={styles.documentClose} onClick={() => closeDocument(document.id)} aria-label={`Close ${document.title}`}>
                      ×
                    </button>
                  </div>
                ))}
              </div>
              {documents.length === 0 ? (
                <div className={styles.emptyDocuments}>
                  <p>No query windows open.</p>
                  <Button size="sm" onClick={() => addDocument({ kind: 'query', sql: '' })}>
                    New Query
                  </Button>
                </div>
              ) : null}
              {documents.map((document) =>
                document.kind === 'query' ? (
                  <QueryWindow
                    key={document.id}
                    title={document.title}
                    request={session.request}
                    target={main.target}
                    databases={databases}
                    initialSql={document.sql}
                    initialDatabase={document.database}
                    runOnOpen={document.runOnOpen}
                    active={active && document.id === activeDocument}
                  />
                ) : document.kind === 'replication' || document.kind === 'storage' || document.kind === 'backups' || document.kind === 'health' ? (
                  <ToolReportView
                    key={document.id}
                    tool={document.kind}
                    sessionId={main.id}
                    active={active && document.id === activeDocument}
                    onOpenQuery={(sql) => addDocument({ kind: 'query', sql })}
                  />
                ) : document.kind === 'orphans' ? (
                  <OrphanedUsers key={document.id} sessionId={main.id} active={active && document.id === activeDocument} />
                ) : (
                  <BlockingManager
                    key={document.id}
                    sessionId={main.id}
                    active={active && document.id === activeDocument}
                    onOpenQuery={(sql) => addDocument({ kind: 'query', sql })}
                  />
                ),
              )}
            </section>
          </div>
        ) : null}
      </div>
    </div>
    </DbEngineContext.Provider>
  )
}
