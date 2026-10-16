import { useState } from 'react'
import { useSearchParams } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { useAuth } from '../../auth/context'
import { hostsApi } from '../../inventory/api/inventoryApi'
import { useResource } from '../../inventory/hooks/useResource'
import { usersApi } from '../api/usersApi'
import { NewUserDialog } from '../components/NewUserDialog'
import { UserEditor } from '../components/UserEditor'
import type { UserDetails } from '../types'
import styles from './UsersPage.module.css'

/** Administrators: user accounts, roles, passwords and host access. The selection is in the URL. */
export function UsersPage() {
  const { user: me } = useAuth()
  const [params, setParams] = useSearchParams()
  const selectedId = params.get('user')
  const users = useResource((signal) => usersApi.list(signal))
  const hosts = useResource((signal) => hostsApi.list(signal))
  const selected = useResource((signal) => (selectedId ? usersApi.get(selectedId, signal) : Promise.resolve(null)), selectedId ?? '')
  const [creating, setCreating] = useState(false)
  const [search, setSearch] = useState('')

  function select(id: string | null) {
    setParams(id ? { user: id } : {}, { replace: true })
  }

  const term = search.trim().toLowerCase()
  const visible = (users.data ?? []).filter((u) => !term || u.userName.toLowerCase().includes(term) || u.email?.toLowerCase().includes(term))

  return (
    <div className={`${styles.page} ${selectedId ? styles.detailOpen : ''}`}>
      <aside className={styles.sidebar} aria-label="Users">
        <div className={styles.sidebarHeader}>
          <h1 className={styles.title}>
            Users {users.data ? <span className={styles.count}>{users.data.length}</span> : null}
          </h1>
          <Button size="sm" onClick={() => setCreating(true)}>
            New user
          </Button>
        </div>
        <div className={styles.searchBox}>
          <input
            className={styles.search}
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search username or email"
            aria-label="Search users"
          />
        </div>
        <div className={styles.listScroll}>
          {users.error && !users.data ? (
            <div className={styles.state}>
              <Alert>{toFormErrors(users.error).message}</Alert>
            </div>
          ) : !users.data ? (
            <div className={styles.state}>
              <Spinner label="Loading users" />
            </div>
          ) : (
            <ul className={styles.list}>
              {visible.map((u) => (
                <li key={u.id}>
                  <button type="button" className={styles.row} aria-current={u.id === selectedId ? 'true' : undefined} onClick={() => select(u.id)}>
                    <span className={styles.avatar} aria-hidden="true">
                      {u.userName.slice(0, 1).toUpperCase()}
                    </span>
                    <span className={styles.rowText}>
                      <span className={styles.rowName}>{u.userName}</span>
                      <span className={styles.rowEmail}>{u.email}</span>
                    </span>
                    <span className={styles.badges}>
                      {u.role === 'Administrator' ? <Badge variant="accent">Admin</Badge> : null}
                      {u.isDisabled ? <Badge>Disabled</Badge> : null}
                      {u.isLockedOut ? <Badge>Locked</Badge> : null}
                      {u.role === 'User' && !u.isDisabled ? <span className={styles.grants}>{u.grantCount === 1 ? '1 grant' : `${u.grantCount} grants`}</span> : null}
                    </span>
                  </button>
                </li>
              ))}
              {visible.length === 0 ? <li className={styles.state}>No users match “{search}”.</li> : null}
            </ul>
          )}
        </div>
      </aside>

      <main className={styles.main}>
        {selectedId ? (
          <button type="button" className={styles.back} onClick={() => select(null)}>
            ‹ All users
          </button>
        ) : null}
        {!selectedId ? (
          <div className={styles.empty}>
            <p className={styles.emptyTitle}>Select a user</p>
            <p>Choose a user to change their role, reset their password or decide which hosts and groups they can access.</p>
          </div>
        ) : selected.error ? (
          <div className={styles.empty}>
            <Alert>{toFormErrors(selected.error).message}</Alert>
          </div>
        ) : !selected.data || !hosts.data || !me ? (
          <div className={styles.empty}>
            <Spinner label="Loading user" />
          </div>
        ) : (
          <UserEditor
            key={selected.data.id}
            user={selected.data}
            hosts={hosts.data}
            currentUserId={me.id}
            onChanged={() => {
              users.reload()
              selected.reload()
            }}
            onDeleted={() => {
              select(null)
              users.reload()
            }}
          />
        )}
      </main>

      {creating ? (
        <NewUserDialog
          onClose={() => setCreating(false)}
          onCreated={(created: UserDetails) => {
            setCreating(false)
            users.reload()
            select(created.id)
          }}
        />
      ) : null}
    </div>
  )
}
