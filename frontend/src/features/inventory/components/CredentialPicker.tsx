import { useState } from 'react'
import { Link } from 'react-router'
import { formatAccount, type Credential } from '../types'
import styles from './CredentialPicker.module.css'

interface CredentialPickerProps {
  credentials: Credential[] | undefined
  value: string[]
  onChange: (ids: string[]) => void
  error?: string
}

const FILTER_THRESHOLD = 8

/** Choose any number of credentials from the shared library. */
export function CredentialPicker({ credentials, value, onChange, error }: CredentialPickerProps) {
  const [filter, setFilter] = useState('')

  function toggle(id: string, checked: boolean) {
    onChange(checked ? [...value, id] : value.filter((v) => v !== id))
  }

  const term = filter.trim().toLowerCase()
  const visible = (credentials ?? []).filter(
    (c) => !term || [c.name, c.username, c.domain].some((field) => field?.toLowerCase().includes(term)),
  )

  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.legend}>
        Credentials{value.length > 0 ? <span className={styles.count}> · {value.length} selected</span> : null}
      </legend>

      {credentials === undefined ? (
        <p className={styles.hint}>Loading credentials…</p>
      ) : credentials.length === 0 ? (
        <p className={styles.hint}>
          No saved credentials yet. Add them under <Link to="/credentials">Credentials</Link>, then attach them here.
        </p>
      ) : (
        <>
          {credentials.length > FILTER_THRESHOLD ? (
            <input
              className={styles.filter}
              type="search"
              value={filter}
              onChange={(event) => setFilter(event.target.value)}
              placeholder="Filter credentials"
              aria-label="Filter credentials"
            />
          ) : null}
          <ul className={styles.list}>
            {visible.map((credential) => {
              const account = formatAccount(credential)
              return (
                <li key={credential.id}>
                  <label className={styles.option}>
                    <input
                      type="checkbox"
                      checked={value.includes(credential.id)}
                      onChange={(event) => toggle(credential.id, event.target.checked)}
                    />
                    <span className={styles.name}>{credential.name}</span>
                    {account ? <span className={styles.account}>{account}</span> : null}
                  </label>
                </li>
              )
            })}
            {visible.length === 0 ? <li className={styles.hint}>No credentials match “{filter}”.</li> : null}
          </ul>
        </>
      )}
      {error ? <p className={styles.error}>{error}</p> : null}
    </fieldset>
  )
}
