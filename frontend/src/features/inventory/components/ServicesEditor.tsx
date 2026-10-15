import { Button } from '../../../components/ui/Button'
import { errorFor } from '../../../lib/formErrors'
import { newServiceDraft, type ServiceDraft } from '../serviceDraft'
import { PROTOCOL_INFO, SERVICE_PROTOCOLS, type ServiceProtocol } from '../types'
import styles from './ServicesEditor.module.css'

interface ServicesEditorProps {
  value: ServiceDraft[]
  onChange: (services: ServiceDraft[]) => void
  /** Field errors from the server, keyed like "services[0].port". */
  errors: Record<string, string>
}

const MAX_SERVICES = 20

/** Ordered list of the ways to connect to a host (the first one is the default). */
export function ServicesEditor({ value, onChange, errors }: ServicesEditorProps) {
  function update(key: number, patch: Partial<ServiceDraft>) {
    onChange(value.map((s) => (s.key === key ? { ...s, ...patch } : s)))
  }

  function move(index: number, offset: -1 | 1) {
    const next = [...value]
    const [item] = next.splice(index, 1)
    next.splice(index + offset, 0, item)
    onChange(next)
  }

  function addService() {
    // Suggest the first protocol not already on the host.
    const unused = SERVICE_PROTOCOLS.find((p) => p !== 'Other' && !value.some((s) => s.protocol === p)) ?? 'Other'
    onChange([...value, newServiceDraft(unused)])
  }

  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.legend}>Services</legend>
      <p className={styles.hint}>How you connect to this host. Leave the port empty to use the default.</p>

      {value.length === 0 ? <p className={styles.empty}>No services yet.</p> : null}

      <ol className={styles.list}>
        {value.map((service, index) => {
          const info = PROTOCOL_INFO[service.protocol]
          const portError = errors[`services[${index}].port`]
          const rowError = errorFor(errors, `services[${index}]`)
          const position = `service ${index + 1}`
          return (
            <li key={service.key} className={styles.row}>
              <select
                className={styles.input}
                value={service.protocol}
                onChange={(event) => update(service.key, { protocol: event.target.value as ServiceProtocol })}
                aria-label={`Type for ${position}`}
              >
                {SERVICE_PROTOCOLS.map((protocol) => (
                  <option key={protocol} value={protocol}>
                    {PROTOCOL_INFO[protocol].name}
                  </option>
                ))}
              </select>
              <input
                className={`${styles.input} ${styles.port}`}
                inputMode="numeric"
                value={service.port}
                onChange={(event) => update(service.key, { port: event.target.value.replace(/\D/g, '').slice(0, 5) })}
                placeholder={info.defaultPort ? String(info.defaultPort) : 'Port'}
                aria-label={`Port for ${position}`}
                aria-invalid={portError ? true : undefined}
              />
              <input
                className={`${styles.input} ${styles.label}`}
                value={service.label}
                onChange={(event) => update(service.key, { label: event.target.value })}
                placeholder="Label (optional)"
                maxLength={100}
                aria-label={`Label for ${position}`}
              />
              <div className={styles.actions}>
                <button
                  type="button"
                  className={styles.iconButton}
                  onClick={() => move(index, -1)}
                  disabled={index === 0}
                  aria-label={`Move ${position} up`}
                >
                  ↑
                </button>
                <button
                  type="button"
                  className={styles.iconButton}
                  onClick={() => move(index, 1)}
                  disabled={index === value.length - 1}
                  aria-label={`Move ${position} down`}
                >
                  ↓
                </button>
                <button
                  type="button"
                  className={styles.iconButton}
                  onClick={() => onChange(value.filter((s) => s.key !== service.key))}
                  aria-label={`Remove ${position}`}
                >
                  ×
                </button>
              </div>
              {rowError ? <p className={styles.error}>{rowError}</p> : null}
            </li>
          )
        })}
      </ol>

      {errors.services ? <p className={styles.error}>{errors.services}</p> : null}

      <div>
        <Button variant="secondary" size="sm" onClick={addService} disabled={value.length >= MAX_SERVICES}>
          Add service
        </Button>
      </div>
    </fieldset>
  )
}
