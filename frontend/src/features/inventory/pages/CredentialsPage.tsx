import { useState } from 'react'
import { Link } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { TextField } from '../../../components/ui/TextField'
import { toFormErrors } from '../../../lib/formErrors'
import { credentialsApi } from '../api/inventoryApi'
import { CredentialFormDialog } from '../components/CredentialFormDialog'
import { useResource } from '../hooks/useResource'
import { formatAccount, type Credential } from '../types'
import styles from './InventoryPage.module.css'

type SortKey = 'name' | 'account' | 'hosts'

export function CredentialsPage() {
  const credentials = useResource((signal) => credentialsApi.list(signal))
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'name', desc: false })
  const [editing, setEditing] = useState<Credential | 'new' | null>(null)

  const term = search.trim().toLowerCase()
  const rows = (credentials.data ?? [])
    .filter((c) => !term || [c.name, c.username, c.domain, c.notes].some((field) => field?.toLowerCase().includes(term)))
    .sort((a, b) => {
      const order =
        sort.key === 'hosts'
          ? a.hostCount - b.hostCount
          : (sort.key === 'name' ? a.name : (formatAccount(a) ?? '')).localeCompare(
              sort.key === 'name' ? b.name : (formatAccount(b) ?? ''),
              undefined,
              { sensitivity: 'base', numeric: true },
            )
      return sort.desc ? -order : order
    })

  function header(key: SortKey, label: string, className?: string) {
    const active = sort.key === key
    return (
      <th scope="col" className={className} aria-sort={active ? (sort.desc ? 'descending' : 'ascending') : undefined}>
        <button
          type="button"
          className={styles.filterLink}
          onClick={() => setSort({ key, desc: active ? !sort.desc : key === 'hosts' })}
        >
          {label} {active ? (sort.desc ? '↓' : '↑') : null}
        </button>
      </th>
    )
  }

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <div>
          <h1 className={styles.title}>Credentials</h1>
          <p className={styles.subtitle}>A shared library of accounts you can attach to any number of hosts.</p>
        </div>
        <Button onClick={() => setEditing('new')}>Add credential</Button>
      </div>

      <TextField
        label="Search credentials"
        hideLabel
        type="search"
        value={search}
        onChange={(event) => setSearch(event.target.value)}
        placeholder="Search name, username, domain…"
      />

      {credentials.error ? <Alert>{toFormErrors(credentials.error).message}</Alert> : null}

      <div className={styles.tableCard}>
        {credentials.data === undefined && credentials.loading ? (
          <div className={styles.state}>
            <Spinner label="Loading credentials" />
          </div>
        ) : rows.length === 0 ? (
          <div className={styles.state}>
            {term ? (
              <p>No credentials match “{search.trim()}”.</p>
            ) : (
              <>
                <p className={styles.stateTitle}>No credentials yet</p>
                <p>Save the accounts you use to sign in to your hosts.</p>
                <Button onClick={() => setEditing('new')}>Add credential</Button>
              </>
            )}
          </div>
        ) : (
          <div className={styles.tableScroll}>
            <table className={styles.table}>
              <thead>
                <tr>
                  {header('name', 'Name')}
                  {header('account', 'Account')}
                  <th scope="col" className={styles.hideSmall}>
                    Secrets
                  </th>
                  {header('hosts', 'Hosts', styles.numeric)}
                </tr>
              </thead>
              <tbody>
                {rows.map((credential) => (
                  <tr key={credential.id}>
                    <td>
                      <button type="button" className={styles.rowLink} onClick={() => setEditing(credential)}>
                        {credential.name}
                      </button>
                      {credential.notes ? <div className={styles.secondary}>{credential.notes}</div> : null}
                    </td>
                    <td className={styles.mono}>{formatAccount(credential) ?? <span className={styles.muted}>—</span>}</td>
                    <td className={styles.hideSmall}>
                      <div className={styles.badges}>
                        {credential.hasPassword ? <Badge>Password</Badge> : null}
                        {credential.hasPrivateKey ? <Badge>SSH key</Badge> : null}
                        {!credential.hasPassword && !credential.hasPrivateKey ? <span className={styles.muted}>None</span> : null}
                      </div>
                    </td>
                    <td className={styles.numeric}>{credential.hostCount}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <p className={styles.muted}>
        Attach credentials to a host from its edit form on the <Link to="/hosts">Hosts</Link> page.
      </p>

      {editing ? (
        <CredentialFormDialog
          credential={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onChanged={credentials.reload}
        />
      ) : null}
    </div>
  )
}
