import { useEffect, useRef, useState, type ChangeEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { api, ApiError } from '../../../lib/api'
import { hostKeyPrompt, type HostKeyPrompt } from '../kvm'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import sessionStyles from './Session.module.css'
import styles from './FileBrowser.module.css'

interface FileBrowserProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
}

interface Entry {
  name: string
  path: string
  kind: 'directory' | 'file' | 'link' | 'other'
  size: number
  modifiedUtc: string
  permissions: string
}

interface ListResponse {
  status: 'ready' | 'trustRequired'
  path: string | null
  parent: string | null
  entries: Entry[] | null
  hostKey: HostKeyPrompt | null
}

const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

function formatSize(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${unit === 0 ? value : value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`
}

/**
 * A host's files over SFTP, through its SSH service (and jump host): browse folders, upload,
 * download, rename, delete and create folders. Every operation goes through NKeeper and is audited.
 */
export function FileBrowser({ session, active, onShowHost }: FileBrowserProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [path, setPath] = useState<string | null>(null)
  const [listing, setListing] = useState<{ path: string; parent: string | null; entries: Entry[] } | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [busy, setBusy] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ text: string; error: boolean } | null>(null)
  const [renaming, setRenaming] = useState<{ entry: Entry; name: string } | null>(null)
  const [deleting, setDeleting] = useState<Entry | null>(null)
  const [newFolder, setNewFolder] = useState<string | null>(null)
  const [conflict, setConflict] = useState<File[] | null>(null)
  const trustRef = useRef<string | undefined>(undefined)
  const uploadRef = useRef<HTMLInputElement>(null)
  const target = session.title

  const login = {
    hostId: session.request.hostId,
    serviceIndex: session.request.serviceIndex,
    credentialId: session.request.credentialId,
    username: session.request.oneOff?.username,
    password: session.request.oneOff?.password,
  }

  useEffect(() => {
    let cancelled = false
    const body = {
      hostId: session.request.hostId,
      serviceIndex: session.request.serviceIndex,
      credentialId: session.request.credentialId,
      username: session.request.oneOff?.username,
      password: session.request.oneOff?.password,
      trustFingerprint: trustRef.current,
      path,
    }
    api.post<ListResponse>('/api/sftp/list', { body }).then(
      (response) => {
        if (cancelled) {
          return
        }
        if (response.status === 'trustRequired' && response.hostKey) {
          setPhase({ kind: 'hostKey', prompt: hostKeyPrompt(response.hostKey), target })
          return
        }
        setListing({ path: response.path!, parent: response.parent, entries: response.entries ?? [] })
        setPhase({ kind: 'connected', target: `${target}:${response.path}` })
      },
      (error: unknown) => {
        if (cancelled) {
          return
        }
        // A folder that can't be opened leaves the last one shown, with the reason.
        if (listing && error instanceof ApiError) {
          setNotice({ text: error.message, error: true })
          return
        }
        setPhase({
          kind: 'ended',
          target,
          code: error instanceof ApiError ? error.code : undefined,
          message: error instanceof ApiError ? error.message : 'Couldn’t open the files.',
        })
      },
    )
    return () => {
      cancelled = true
    }
    // `listing` is read only to decide how to show an error.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.request, path, attempt, target])

  function open(next: string | null) {
    setNotice(null)
    setPath(next)
    setAttempt((n) => n + 1)
  }

  const refresh = () => setAttempt((n) => n + 1)

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      setPhase({ kind: 'ended', target, code: 'host_key_rejected', message: 'The host key was not trusted, so NKeeper didn’t connect.' })
      return
    }
    trustRef.current = phase.prompt.fingerprint
    setPhase({ kind: 'connecting', target })
    refresh()
  }

  /** Runs a file operation, then shows the folder again. */
  async function run(label: string, action: () => Promise<unknown>, done?: string) {
    setBusy(label)
    setNotice(null)
    try {
      await action()
      if (done) {
        setNotice({ text: done, error: false })
      }
      refresh()
    } catch (error) {
      setNotice({ text: error instanceof ApiError ? error.message : `${label} failed.`, error: true })
    } finally {
      setBusy(null)
    }
  }

  async function upload(files: File[], overwrite: boolean) {
    if (!listing || files.length === 0) {
      return
    }
    const form = new FormData()
    form.append('hostId', login.hostId)
    form.append('serviceIndex', String(login.serviceIndex))
    if (login.credentialId) {
      form.append('credentialId', login.credentialId)
    }
    if (login.username) {
      form.append('username', login.username)
      form.append('password', login.password ?? '')
    }
    form.append('directory', listing.path)
    form.append('overwrite', overwrite ? 'true' : 'false')
    files.forEach((file) => form.append('files', file, file.name))
    setBusy('Uploading')
    setNotice(null)
    try {
      await api.post('/api/sftp/upload', { body: form })
      setConflict(null)
      setNotice({ text: `Uploaded ${files.length === 1 ? files[0].name : `${files.length} files`}.`, error: false })
      refresh()
    } catch (error) {
      if (error instanceof ApiError && error.code === 'file_exists') {
        setConflict(files)
        setNotice({ text: `${error.message} Replace it?`, error: true })
      } else {
        setNotice({ text: error instanceof ApiError ? error.message : 'The upload failed.', error: true })
      }
    } finally {
      setBusy(null)
    }
  }

  function chooseFiles(event: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(event.target.files ?? [])
    event.target.value = ''
    void upload(files, false)
  }

  async function download(entry: Entry) {
    setBusy('Downloading')
    try {
      const { url } = await api.post<{ url: string }>('/api/sftp/download-link', { body: { ...login, path: entry.path } })
      // The browser streams the file to disk from a one-time link.
      const link = document.createElement('a')
      link.href = `${import.meta.env.VITE_API_BASE_URL ?? ''}${url}`
      link.download = entry.name
      link.click()
    } catch (error) {
      setNotice({ text: error instanceof ApiError ? error.message : 'The download failed.', error: true })
    } finally {
      setBusy(null)
    }
  }

  const segments = listing ? listing.path.split('/').filter(Boolean) : []

  return (
    <div className={sessionStyles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} files`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <Button variant="ghost" size="sm" onClick={refresh} disabled={busy !== null}>
            Refresh
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={`${sessionStyles.terminalArea} ${sessionStyles.webArea}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={refresh} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
        {phase.kind === 'connected' && listing ? (
          <div className={styles.browser}>
            <div className={styles.toolbar}>
              <nav className={styles.crumbs} aria-label="Folder">
                <button type="button" className={styles.crumb} onClick={() => open('/')}>
                  /
                </button>
                {segments.map((segment, index) => (
                  <span key={index}>
                    <button type="button" className={styles.crumb} onClick={() => open(`/${segments.slice(0, index + 1).join('/')}`)}>
                      {segment}
                    </button>
                    {index < segments.length - 1 ? '/' : null}
                  </span>
                ))}
              </nav>
              <div className={styles.actions}>
                <Button size="sm" variant="secondary" disabled={!listing.parent || busy !== null} onClick={() => open(listing.parent)}>
                  Up
                </Button>
                <Button size="sm" variant="secondary" disabled={busy !== null} onClick={() => setNewFolder('')}>
                  New folder
                </Button>
                <Button size="sm" loading={busy === 'Uploading'} disabled={busy !== null} onClick={() => uploadRef.current?.click()}>
                  Upload
                </Button>
                <input ref={uploadRef} type="file" multiple hidden onChange={chooseFiles} aria-label="Files to upload" />
              </div>
            </div>

            {notice ? (
              <div className={styles.notice}>
                <Alert variant={notice.error ? 'error' : 'info'}>{notice.text}</Alert>
                {conflict ? (
                  <>
                    <Button size="sm" variant="danger" onClick={() => void upload(conflict, true)}>
                      Replace
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => (setConflict(null), setNotice(null))}>
                      Cancel
                    </Button>
                  </>
                ) : null}
              </div>
            ) : null}

            {newFolder !== null ? (
              <form
                className={styles.inline}
                onSubmit={(event) => {
                  event.preventDefault()
                  const name = newFolder.trim()
                  if (name) {
                    setNewFolder(null)
                    void run('Creating the folder', () => api.post('/api/sftp/mkdir', { body: { ...login, path: `${listing.path.replace(/\/$/, '')}/${name}` } }))
                  }
                }}
              >
                <input
                  className={styles.input}
                  value={newFolder}
                  onChange={(event) => setNewFolder(event.target.value)}
                  placeholder="Folder name"
                  aria-label="New folder name"
                  autoFocus
                />
                <Button type="submit" size="sm">
                  Create
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setNewFolder(null)}>
                  Cancel
                </Button>
              </form>
            ) : null}

            {listing.entries.length === 0 ? (
              <p className={styles.empty}>This folder is empty.</p>
            ) : (
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col">Size</th>
                    <th scope="col">Modified</th>
                    <th scope="col">Permissions</th>
                    <th scope="col">
                      <span className="visually-hidden">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {listing.entries.map((entry) => (
                    <tr key={entry.path}>
                      <td className={styles.name}>
                        {renaming?.entry === entry ? (
                          <form
                            className={styles.inline}
                            onSubmit={(event) => {
                              event.preventDefault()
                              const name = renaming.name.trim()
                              setRenaming(null)
                              if (name && name !== entry.name) {
                                void run('Renaming', () => api.post('/api/sftp/rename', { body: { ...login, path: entry.path, newPath: name } }))
                              }
                            }}
                          >
                            <input
                              className={styles.input}
                              value={renaming.name}
                              onChange={(event) => setRenaming({ entry, name: event.target.value })}
                              aria-label={`New name for ${entry.name}`}
                              autoFocus
                            />
                            <Button type="submit" size="sm">
                              Rename
                            </Button>
                          </form>
                        ) : entry.kind === 'directory' ? (
                          <button type="button" className={styles.folder} onClick={() => open(entry.path)}>
                            📁 {entry.name}
                          </button>
                        ) : (
                          <span>📄 {entry.name}</span>
                        )}
                      </td>
                      <td>{entry.kind === 'directory' ? '' : formatSize(entry.size)}</td>
                      <td>{timeFormat.format(new Date(entry.modifiedUtc))}</td>
                      <td className={styles.mono}>{entry.permissions}</td>
                      <td className={styles.rowActions}>
                        {deleting === entry ? (
                          <>
                            <Button
                              size="sm"
                              variant="danger"
                              onClick={() => {
                                setDeleting(null)
                                void run('Deleting', () => api.post('/api/sftp/delete', { body: { ...login, path: entry.path } }), `Deleted ${entry.name}.`)
                              }}
                            >
                              Delete {entry.name}
                            </Button>
                            <Button size="sm" variant="ghost" onClick={() => setDeleting(null)}>
                              Keep
                            </Button>
                          </>
                        ) : (
                          <>
                            {entry.kind === 'file' ? (
                              <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => void download(entry)}>
                                Download
                              </Button>
                            ) : null}
                            <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => setRenaming({ entry, name: entry.name })}>
                              Rename
                            </Button>
                            <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => setDeleting(entry)}>
                              Delete
                            </Button>
                          </>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        ) : null}
      </div>
    </div>
  )
}
