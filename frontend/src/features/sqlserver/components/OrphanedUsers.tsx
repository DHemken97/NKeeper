import { useCallback, useEffect, useState, type MouseEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { ContextMenu, type MenuEntry } from '../../../components/ui/ContextMenu'
import { Dialog } from '../../../components/ui/Dialog'
import { PasswordField } from '../../../components/ui/PasswordField'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { sqlServerApi, type OrphanFix, type OrphanReport, type OrphanedUser } from '../sqlServerApi'
import styles from './SqlServer.module.css'

interface OrphanedUsersProps {
  /** The SQL Server tab's own session. */
  sessionId: string
  active: boolean
}

/** A fix that needs a choice or a confirmation first. */
type Pending =
  | { kind: 'map'; user: OrphanedUser }
  | { kind: 'create'; user: OrphanedUser }
  | { kind: 'drop'; user: OrphanedUser }
  | { kind: 'owner'; database: string; ownerSid: string }
  | { kind: 'bulkDrop'; users: OrphanedUser[] }

/** A user's identity in the list (names repeat across databases). */
const keyOf = (user: OrphanedUser) => `${user.database}/${user.user}`

/**
 * Orphaned users: database users whose login is gone or no longer has their SID, as after restoring a
 * database on another server, so no one can sign in as them. Each can be mapped to a login (the one of
 * its own name, usually), given a new login with its SID, or dropped. Databases whose owner login is
 * missing get a new owner.
 */
export function OrphanedUsers({ sessionId, active }: OrphanedUsersProps) {
  const [report, setReport] = useState<OrphanReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [menu, setMenu] = useState<{ x: number; y: number; user: OrphanedUser } | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [login, setLogin] = useState('')
  const [password, setPassword] = useState('')
  const [checkPolicy, setCheckPolicy] = useState(true)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [failures, setFailures] = useState<{ database: string; user: string; message: string }[]>([])

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      setReport(await sqlServerApi.orphans(sessionId))
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Couldn’t check the databases.')
    } finally {
      setLoading(false)
    }
  }, [sessionId])

  useEffect(() => {
    if (!active) {
      return
    }
    const timer = window.setTimeout(() => void refresh(), 0)
    return () => window.clearTimeout(timer)
  }, [active, refresh])

  const users = report?.users ?? []
  const owners = report?.owners ?? []
  const matched = users.filter((u) => u.matchingLogin)
  // Only users still in the list count as selected (a fix or refresh removes the others).
  const chosen = users.filter((u) => selected.has(keyOf(u)))
  const chosenMatched = chosen.filter((u) => u.matchingLogin)
  const allChosen = users.length > 0 && chosen.length === users.length

  function select(which: OrphanedUser[]) {
    setSelected(new Set(which.map(keyOf)))
  }

  function toggle(user: OrphanedUser) {
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(keyOf(user))) {
        next.delete(keyOf(user))
      } else {
        next.add(keyOf(user))
      }
      return next
    })
  }

  /** Maps or drops the chosen users; the ones that couldn't be stay listed, with why. */
  async function applyMany(action: 'map' | 'drop', targets: OrphanedUser[]) {
    setBusy(true)
    setDialogError(null)
    try {
      const result = await sqlServerApi.fixManyOrphans(
        sessionId,
        targets.map((u) => ({ action, database: u.database, user: u.user })),
      )
      setReport(result.report)
      setNotice(result.message)
      setFailures(result.results.filter((r) => !r.succeeded))
      setSelected(new Set())
      setPending(null)
      setError(null)
    } catch (e) {
      const message = e instanceof ApiError ? e.message : 'Couldn’t fix those users.'
      if (pending) {
        setDialogError(message)
      } else {
        setError(message)
      }
    } finally {
      setBusy(false)
    }
  }

  /** Logins a user can be mapped to: SQL logins for SQL users, Windows ones for Windows users and groups. */
  const loginsFor = (user: OrphanedUser) => (report?.logins ?? []).filter((l) => l.windows === (user.type !== 'SQL user'))

  function open(next: Pending) {
    setMenu(null)
    setDialogError(null)
    setPassword('')
    setCheckPolicy(true)
    setLogin(
      next.kind === 'map'
        ? (next.user.matchingLogin ?? loginsFor(next.user)[0]?.name ?? '')
        : next.kind === 'create'
          ? next.user.user
          : next.kind === 'owner'
            ? ((report?.logins.find((l) => l.name === 'sa') ?? report?.logins.find((l) => !l.windows))?.name ?? '')
            : '',
    )
    setPending(next)
  }

  async function apply(fix: OrphanFix) {
    setBusy(true)
    setDialogError(null)
    try {
      const result = await sqlServerApi.fixOrphan(sessionId, fix)
      setReport(result.report)
      setNotice(result.message)
      setFailures([])
      setPending(null)
    } catch (e) {
      const message = e instanceof ApiError ? e.message : 'Couldn’t fix that.'
      if (pending) {
        setDialogError(message)
      } else {
        setError(message)
      }
    } finally {
      setBusy(false)
    }
  }

  async function fixMatched() {
    setBusy(true)
    try {
      const result = await sqlServerApi.fixMatchedOrphans(sessionId)
      setReport(result.report)
      setNotice(result.message)
      setFailures([])
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Couldn’t map the users.')
    } finally {
      setBusy(false)
    }
  }

  function confirm() {
    if (!pending) {
      return
    }
    if (pending.kind === 'bulkDrop') {
      void applyMany('drop', pending.users)
    } else if (pending.kind === 'owner') {
      void apply({ action: 'owner', database: pending.database, login })
    } else if (pending.kind === 'map') {
      void apply({ action: 'map', database: pending.user.database, user: pending.user.user, login })
    } else if (pending.kind === 'create') {
      void apply({ action: 'create', database: pending.user.database, user: pending.user.user, login: login.trim(), password, checkPolicy })
    } else {
      void apply({ action: 'drop', database: pending.user.database, user: pending.user.user })
    }
  }

  function menuEntries(user: OrphanedUser): MenuEntry[] {
    return [
      { kind: 'header', label: `${user.database} · ${user.user}` },
      ...(user.matchingLogin
        ? [{ kind: 'item', label: `Map to ${user.matchingLogin}`, onSelect: () => void apply({ action: 'map', database: user.database, user: user.user }) } as const]
        : []),
      { kind: 'item', label: 'Map to a login…', disabled: loginsFor(user).length === 0, onSelect: () => open({ kind: 'map', user }) },
      { kind: 'item', label: 'Create its login…', disabled: Boolean(user.matchingLogin), detail: user.matchingLogin ? 'Its login exists' : undefined, onSelect: () => open({ kind: 'create', user }) },
      { kind: 'separator' },
      { kind: 'item', label: 'Drop user…', onSelect: () => open({ kind: 'drop', user }) },
    ]
  }

  function openMenu(event: MouseEvent, user: OrphanedUser) {
    event.preventDefault()
    setMenu({ x: event.clientX, y: event.clientY, user })
  }

  const createsSqlLogin = pending?.kind === 'create' && pending.user.type === 'SQL user'
  const canConfirm =
    !busy &&
    (pending?.kind === 'bulkDrop'
      ? pending.users.length > 0
      : pending?.kind === 'drop'
      ? pending.user.ownedSchemas.length === 0
      : pending?.kind === 'create'
        ? login.trim() !== '' && (!createsSqlLogin || password !== '')
        : login !== '')

  return (
    <div className={styles.blocking} hidden={!active} role="tabpanel" aria-label="Orphaned Users">
      <header className={styles.blockingHeader}>
        <div>
          <h2>Orphaned Users</h2>
          <p>Database users whose login is missing or no longer matches, as after restoring a database on another server</p>
        </div>
        <div className={styles.blockingActions}>
          <span className={styles.toolbarNote}>{report ? `${report.databasesChecked} databases checked` : null}</span>
          <Button size="sm" variant="secondary" onClick={() => void refresh()} loading={loading}>
            Refresh
          </Button>
          <Button size="sm" onClick={() => void fixMatched()} disabled={busy || matched.length === 0} title="ALTER USER … WITH LOGIN, for each user that has a login of its own name">
            Fix all matched by name ({matched.length})
          </Button>
        </div>
      </header>

      {notice ? <Alert variant="info">{notice}</Alert> : null}
      {failures.length > 0 ? (
        <Alert variant="error">
          <ul className={styles.failures} aria-label="Couldn’t fix">
            {failures.map((f) => (
              <li key={`${f.database}/${f.user}`}>
                <strong>
                  {f.database} · {f.user}
                </strong>
                : {f.message}
              </li>
            ))}
          </ul>
        </Alert>
      ) : null}
      {error ? <Alert variant="error">{error}</Alert> : null}
      {report && report.skipped.length > 0 ? (
        <details className={styles.skipped}>
          <summary>
            {report.skipped.length} {report.skipped.length === 1 ? 'database wasn’t' : 'databases weren’t'} checked
          </summary>
          <ul>
            {report.skipped.map((reason) => (
              <li key={reason}>{reason}</li>
            ))}
          </ul>
        </details>
      ) : null}

      {report && users.length === 0 && owners.length === 0 ? <p className={styles.blockingEmpty}>No orphaned users: every database user maps to a login.</p> : null}

      {users.length > 0 ? (
        <div className={styles.blockingTableWrap}>
          <div className={styles.bulkBar} role="toolbar" aria-label="Selected users">
            <span className={styles.toolbarNote}>Select:</span>
            <Button size="sm" variant="ghost" onClick={() => select(users)}>
              All
            </Button>
            <Button size="sm" variant="ghost" onClick={() => select(users.filter((u) => !u.matchingLogin))} disabled={users.every((u) => u.matchingLogin)}>
              Without a login of their name
            </Button>
            <Button size="sm" variant="ghost" onClick={() => select(matched)} disabled={matched.length === 0}>
              With a login of their name
            </Button>
            <Button size="sm" variant="ghost" onClick={() => select([])} disabled={chosen.length === 0}>
              None
            </Button>
            <span className={styles.bulkActions}>
              <span className={styles.toolbarNote}>{chosen.length} selected</span>
              <Button size="sm" variant="secondary" onClick={() => void applyMany('map', chosenMatched)} disabled={busy || chosenMatched.length === 0}>
                Map selected to their logins ({chosenMatched.length})
              </Button>
              <Button size="sm" variant="danger" onClick={() => open({ kind: 'bulkDrop', users: chosen })} disabled={busy || chosen.length === 0}>
                Drop selected ({chosen.length})…
              </Button>
            </span>
          </div>
          <table className={styles.blockingTable} aria-label="Orphaned users">
            <thead>
              <tr>
                <th scope="col" className={styles.checkCell}>
                  <input
                    type="checkbox"
                    checked={allChosen}
                    ref={(box) => {
                      if (box) {
                        box.indeterminate = chosen.length > 0 && !allChosen
                      }
                    }}
                    onChange={() => select(allChosen ? [] : users)}
                    aria-label="Select all users"
                  />
                </th>
                <th scope="col">Database</th>
                <th scope="col">User</th>
                <th scope="col">Type</th>
                <th scope="col">Default Schema</th>
                <th scope="col">Login of the Same Name</th>
                <th scope="col">Fix</th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr
                  key={`${user.database}/${user.user}`}
                  tabIndex={0}
                  onContextMenu={(event) => openMenu(event, user)}
                  onKeyDown={(event) => {
                    if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
                      event.preventDefault()
                      const rect = event.currentTarget.getBoundingClientRect()
                      setMenu({ x: rect.left + 40, y: rect.bottom, user })
                    }
                  }}
                >
                  <td className={styles.checkCell}>
                    <input type="checkbox" checked={selected.has(keyOf(user))} onChange={() => toggle(user)} aria-label={`Select ${user.user} in ${user.database}`} />
                  </td>
                  <td>{user.database}</td>
                  <td>
                    {user.user}
                    {user.ownedSchemas.length > 0 ? <span className={styles.waitInfo}>owns {user.ownedSchemas.join(', ')}</span> : null}
                  </td>
                  <td>{user.type}</td>
                  <td>{user.defaultSchema ?? '—'}</td>
                  <td>{user.matchingLogin ?? <span className={styles.treeDetail}>none</span>}</td>
                  <td className={styles.fixCell}>
                    {user.matchingLogin ? (
                      <Button size="sm" onClick={() => void apply({ action: 'map', database: user.database, user: user.user })} disabled={busy}>
                        Map to {user.matchingLogin}
                      </Button>
                    ) : (
                      <Button size="sm" onClick={() => open({ kind: 'create', user })} disabled={busy}>
                        Create login…
                      </Button>
                    )}
                    <Button
                      size="sm"
                      variant="ghost"
                      onClick={(event) => {
                        const rect = event.currentTarget.getBoundingClientRect()
                        setMenu({ x: rect.left, y: rect.bottom, user })
                      }}
                      aria-label={`More fixes for ${user.user} in ${user.database}`}
                    >
                      More…
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className={styles.hint}>Right-click a user (or press Shift+F10) for every fix.</p>
        </div>
      ) : null}

      {owners.length > 0 ? (
        <div className={styles.blockingTableWrap}>
          <h3 className={styles.sectionTitle}>Databases whose owner login is missing</h3>
          <table className={styles.blockingTable} aria-label="Databases without an owner">
            <thead>
              <tr>
                <th scope="col">Database</th>
                <th scope="col">Owner SID</th>
                <th scope="col">Fix</th>
              </tr>
            </thead>
            <tbody>
              {owners.map((owner) => (
                <tr key={owner.database}>
                  <td>{owner.database}</td>
                  <td className={styles.sqlCell}>{owner.ownerSid}</td>
                  <td>
                    <Button size="sm" onClick={() => open({ kind: 'owner', ...owner })} disabled={busy}>
                      Set owner…
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}

      {menu ? <ContextMenu label={`Fixes for ${menu.user.user}`} entries={menuEntries(menu.user)} x={menu.x} y={menu.y} onClose={() => setMenu(null)} /> : null}

      {pending ? (
        <Dialog
          title={
            pending.kind === 'bulkDrop'
              ? `Drop ${pending.users.length} ${pending.users.length === 1 ? 'user' : 'users'}?`
              : pending.kind === 'owner'
              ? `Set the owner of ${pending.database}`
              : pending.kind === 'map'
                ? `Map ${pending.user.user} to a login`
                : pending.kind === 'create'
                  ? `Create a login for ${pending.user.user}`
                  : `Drop ${pending.user.user}?`
          }
          description={pending.kind === 'owner' || pending.kind === 'bulkDrop' ? undefined : `In ${pending.user.database}`}
          onClose={() => !busy && setPending(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setPending(null)} disabled={busy}>
                Cancel
              </Button>
              <Button variant={pending.kind === 'drop' ? 'danger' : 'primary'} onClick={confirm} disabled={!canConfirm} loading={busy}>
                {pending.kind === 'bulkDrop'
                  ? `Drop ${pending.users.length} ${pending.users.length === 1 ? 'user' : 'users'}`
                  : pending.kind === 'owner'
                    ? 'Set owner'
                    : pending.kind === 'map'
                      ? 'Map'
                      : pending.kind === 'create'
                        ? 'Create and map'
                        : 'Drop user'}
              </Button>
            </>
          }
        >
          {pending.kind === 'map' || pending.kind === 'owner' ? (
            <label className={styles.dialogField}>
              <span>Login</span>
              <select value={login} onChange={(event) => setLogin(event.target.value)} aria-label="Login">
                {(pending.kind === 'map' ? loginsFor(pending.user) : (report?.logins ?? [])).map((l) => (
                  <option key={l.name} value={l.name}>
                    {l.name}
                    {l.disabled ? ' (disabled)' : ''}
                  </option>
                ))}
              </select>
            </label>
          ) : null}
          {pending.kind === 'map' ? <p className={styles.hint}>ALTER USER … WITH LOGIN: the user keeps its permissions and schemas, and signs in as this login.</p> : null}
          {pending.kind === 'owner' ? <p className={styles.hint}>ALTER AUTHORIZATION ON DATABASE: the login becomes the database’s owner (its dbo).</p> : null}

          {pending.kind === 'create' ? (
            <>
              <TextField label="Login name" value={login} onChange={(event) => setLogin(event.target.value)} maxLength={128} spellCheck={false} />
              {createsSqlLogin ? (
                <>
                  <PasswordField label="Password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="new-password" />
                  <Checkbox label="Enforce password policy" checked={checkPolicy} onChange={(event) => setCheckPolicy(event.target.checked)} />
                  <p className={styles.hint}>
                    The login gets the user’s SID ({pending.user.sid.length > 24 ? `${pending.user.sid.slice(0, 24)}…` : pending.user.sid}), so this user in other
                    databases restored from the same server maps to it too.
                  </p>
                </>
              ) : (
                <p className={styles.hint}>CREATE LOGIN … FROM WINDOWS: the account must exist in the domain. The user is then mapped to it.</p>
              )}
            </>
          ) : null}

          {pending.kind === 'bulkDrop' ? (
            <>
              <p>DROP USER removes each from its database, with its permissions there. Their objects and data stay.</p>
              <ul className={styles.bulkList}>
                {pending.users.map((u) => (
                  <li key={keyOf(u)}>
                    {u.database} · {u.user}
                  </li>
                ))}
              </ul>
              {pending.users.some((u) => u.ownedSchemas.length > 0) ? (
                <Alert variant="error">
                  {pending.users.filter((u) => u.ownedSchemas.length > 0).map((u) => u.user).join(', ')} own schemas, so they can’t be dropped and are left as
                  they are. The others are dropped.
                </Alert>
              ) : null}
            </>
          ) : null}
          {pending.kind === 'drop' ? (
            pending.user.ownedSchemas.length > 0 ? (
              <Alert variant="error">
                {pending.user.user} owns {pending.user.ownedSchemas.join(', ')}, so it can’t be dropped. Give those schemas another owner first, or map the user to
                a login instead.
              </Alert>
            ) : (
              <p>DROP USER removes {pending.user.user} from {pending.user.database}, with its permissions there. Its objects and data stay.</p>
            )
          ) : null}
          {dialogError ? <Alert variant="error">{dialogError}</Alert> : null}
        </Dialog>
      ) : null}
    </div>
  )
}
