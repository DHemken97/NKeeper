import { useId, useState, type KeyboardEvent } from 'react'
import { Button } from '../../../components/ui/Button'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import panel from '../../inventory/components/HostPanel.module.css'
import { zabbixApi, type ZabbixHostSummary } from '../zabbixApi'
import styles from './Zabbix.module.css'

interface ZabbixHostFieldProps {
  /** The Zabbix host ID, or empty when not linked. */
  value: string
  onChange: (zabbixHostId: string) => void
  error?: string
  /** What to search Zabbix for at first: the host's name or address. */
  suggestion: string
}

/** The Zabbix host this one is linked to: its ID, typed in or found by searching Zabbix. */
export function ZabbixHostField({ value, onChange, error, suggestion }: ZabbixHostFieldProps) {
  const searchId = useId()
  const [searching, setSearching] = useState(false)
  const [text, setText] = useState('')
  const [results, setResults] = useState<ZabbixHostSummary[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)

  async function search(query: string) {
    setBusy(true)
    setProblem(null)
    try {
      setResults(await zabbixApi.search(query.trim()))
    } catch (err) {
      setResults(null)
      setProblem(
        err instanceof ApiError && err.code === 'zabbix_disabled'
          ? 'Zabbix isn’t set up. An administrator can set it up under Settings.'
          : toFormErrors(err).message ?? 'Zabbix couldn’t be searched.',
      )
    } finally {
      setBusy(false)
    }
  }

  function open() {
    setSearching(true)
    setText(suggestion)
    if (suggestion.trim()) {
      void search(suggestion)
    }
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    // Enter searches instead of saving the host.
    if (event.key === 'Enter') {
      event.preventDefault()
      void search(text)
    }
  }

  return (
    <div className={panel.full}>
      <div className={styles.idRow}>
        <TextField
          label="Zabbix host ID"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          error={error}
          hint="Links this host to Zabbix, to show what Zabbix knows about it."
          inputMode="numeric"
          maxLength={20}
          autoComplete="off"
          spellCheck={false}
          placeholder="Not linked"
        />
        <div className={styles.idActions}>
          <Button variant="secondary" size="sm" onClick={open} disabled={searching}>
            Find in Zabbix…
          </Button>
          {value ? (
            <Button variant="ghost" size="sm" onClick={() => onChange('')}>
              Unlink
            </Button>
          ) : null}
        </div>
      </div>
      {searching ? (
        <div className={styles.search} role="group" aria-labelledby={searchId}>
          <span id={searchId} className="visually-hidden">
            Find in Zabbix
          </span>
          <div className={styles.searchBar}>
            <TextField
              label="Search Zabbix"
              hideLabel
              value={text}
              onChange={(e) => setText(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Name, IP address or DNS name"
              maxLength={255}
              autoComplete="off"
            />
            <Button size="sm" onClick={() => void search(text)} loading={busy} disabled={!text.trim()}>
              Search
            </Button>
            <Button variant="ghost" size="sm" onClick={() => setSearching(false)}>
              Close
            </Button>
          </div>
          {problem ? <p className={panel.fieldError}>{problem}</p> : null}
          {results && results.length === 0 ? <p className={panel.muted}>No Zabbix hosts match.</p> : null}
          {results && results.length > 0 ? (
            <ul className={styles.results}>
              {results.map((result) => (
                <li key={result.hostId}>
                  <button
                    type="button"
                    className={styles.result}
                    aria-pressed={result.hostId === value}
                    onClick={() => {
                      onChange(result.hostId)
                      setSearching(false)
                    }}
                  >
                    <span>
                      {result.name}
                      {result.name !== result.host ? <span className={panel.muted}> ({result.host})</span> : null}
                      {!result.monitored ? <span className={panel.muted}> · not monitored</span> : null}
                    </span>
                    <span className={`${panel.mono} ${panel.muted}`}>
                      {[...result.ipAddresses, ...result.dnsNames].join(', ') || '—'} · ID {result.hostId}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}
