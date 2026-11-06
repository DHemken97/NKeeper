import type { ReactNode } from 'react'
import { useInventoryLimits } from '../limits'
import { Button } from '../../../components/ui/Button'
import { errorFor } from '../../../lib/formErrors'
import { newServiceDraft, type ServiceDraft } from '../drafts'
import { hasTool, PROTOCOL_INFO, RDP_SECURITY_INFO, SERVICE_TOOLS, SUPPORTED_PROTOCOLS, type HostService, type RdpSecurity, type ServiceProtocol, type ServiceTool } from '../types'
import styles from './ListEditor.module.css'

interface ServicesEditorProps {
  value: ServiceDraft[]
  onChange: (services: ServiceDraft[]) => void
  /** Field errors from the server, keyed like "services[0].port". */
  errors: Record<string, string>
  /** Services of types NKeeper can't connect with, left out of the draft: saving removes them. */
  dropped?: HostService[]
  /** The service types offered; every supported one by default. */
  protocols?: readonly ServiceProtocol[]
  hint?: ReactNode
  emptyText?: string
  /** Whether services can be put in order (the first one is the host's default). */
  reorderable?: boolean
  /** A note under a service, e.g. how many hosts it would change. */
  note?: (service: ServiceDraft) => string | null
}


/** Ordered list of the ways to connect to a host (the first one is the default). */
export function ServicesEditor({
  value,
  onChange,
  errors,
  dropped = [],
  protocols = SUPPORTED_PROTOCOLS,
  hint = 'How you connect to this host. Leave the port empty to use the default. An iDRAC has its own address; its port is the HTTPS (Redfish) port. A SQL Server named instance without a port is found with the SQL Server Browser. An Oracle service needs its service name (or SID).',
  emptyText = 'No services yet.',
  reorderable = true,
  note,
}: ServicesEditorProps) {
  const limits = useInventoryLimits()
  function update(key: number, patch: Partial<ServiceDraft>) {
    onChange(value.map((s) => (s.key === key ? { ...s, ...patch } : s)))
  }

  /** Turns a tool on or off; with all of them on, it's stored as "all", so tools added later start on. */
  function toggleTool(service: ServiceDraft, tool: ServiceTool) {
    const all = (SERVICE_TOOLS[service.protocol] ?? []).map((t) => t.tool as string)
    const current = service.tools ?? all
    const next = current.includes(tool) ? current.filter((t) => t !== tool) : [...current, tool]
    update(service.key, { tools: all.every((t) => next.includes(t)) ? null : next })
  }

  function move(index: number, offset: -1 | 1) {
    const next = [...value]
    const [item] = next.splice(index, 1)
    next.splice(index + offset, 0, item)
    onChange(next)
  }

  function addService() {
    // Suggest the first protocol not already on the host.
    const unused = protocols.find((p) => !value.some((s) => s.protocol === p)) ?? protocols[0]
    onChange([...value, newServiceDraft(unused)])
  }

  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.legend}>Services</legend>
      <p className={styles.hint}>{hint}</p>

      {dropped.length > 0 ? (
        <p className={styles.error} role="note">
          NKeeper can’t connect with {[...new Set(dropped.map((s) => PROTOCOL_INFO[s.protocol].name))].join(', ')}, so{' '}
          {dropped.length === 1 ? 'that service is' : 'those services are'} removed when you save. Cancel to keep{' '}
          {dropped.length === 1 ? 'it' : 'them'}.
        </p>
      ) : null}

      {value.length === 0 ? <p className={styles.empty}>{emptyText}</p> : null}

      <ol className={styles.list}>
        {value.map((service, index) => {
          const info = PROTOCOL_INFO[service.protocol]
          const portError = errors[`services[${index}].port`]
          const rowError = errorFor(errors, `services[${index}]`)
          const position = `service ${index + 1}`
          const serviceNote = note?.(service)
          return (
            <li key={service.key} className={styles.row}>
              <select
                className={styles.input}
                value={service.protocol}
                onChange={(event) => update(service.key, { protocol: event.target.value as ServiceProtocol, tools: null })}
                aria-label={`Type for ${position}`}
              >
                {protocols.map((protocol) => (
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
                {reorderable ? (
                  <>
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
                  </>
                ) : null}
                <button
                  type="button"
                  className={styles.iconButton}
                  onClick={() => onChange(value.filter((s) => s.key !== service.key))}
                  aria-label={`Remove ${position}`}
                >
                  ×
                </button>
              </div>
              {service.protocol === 'Rdp' ? (
                <label className={styles.option}>
                  <span>Security</span>
                  <select
                    className={styles.input}
                    value={service.rdpSecurity}
                    onChange={(event) => update(service.key, { rdpSecurity: event.target.value as RdpSecurity })}
                    aria-label={`Security for ${position}`}
                  >
                    {Object.entries(RDP_SECURITY_INFO).map(([mode, info]) => (
                      <option key={mode} value={mode}>
                        {info.label} — {info.description}
                      </option>
                    ))}
                  </select>
                </label>
              ) : null}
              {service.protocol === 'Idrac' ? (
                <label className={styles.option}>
                  <span>iDRAC address</span>
                  <input
                    className={styles.input}
                    value={service.address}
                    onChange={(event) => update(service.key, { address: event.target.value })}
                    placeholder="e.g. 10.0.10.21"
                    maxLength={253}
                    required
                    spellCheck={false}
                    aria-label={`iDRAC address for ${position}`}
                    aria-invalid={errors[`services[${index}].address`] ? true : undefined}
                  />
                </label>
              ) : null}
              {service.protocol === 'Ssms' ? (
                <label className={styles.option}>
                  <span>Instance</span>
                  <input
                    className={styles.input}
                    value={service.instance}
                    onChange={(event) => update(service.key, { instance: event.target.value })}
                    placeholder="Default instance (MSSQLSERVER)"
                    maxLength={16}
                    spellCheck={false}
                    aria-label={`Instance for ${position}`}
                    aria-invalid={errors[`services[${index}].instance`] ? true : undefined}
                  />
                </label>
              ) : null}
              {service.protocol === 'Oracle' ? (
                <label className={styles.option}>
                  <span>Service name</span>
                  <input
                    className={styles.input}
                    value={service.instance}
                    onChange={(event) => update(service.key, { instance: event.target.value })}
                    placeholder="e.g. ORCLPDB1 or XE"
                    maxLength={128}
                    required
                    spellCheck={false}
                    aria-label={`Service name for ${position}`}
                    aria-invalid={errors[`services[${index}].instance`] ? true : undefined}
                  />
                </label>
              ) : null}
              {SERVICE_TOOLS[service.protocol] ? (
                <div className={styles.option} role="group" aria-label={`Tools for ${position}`}>
                  <span>Tools</span>
                  {SERVICE_TOOLS[service.protocol]!.map(({ tool, label }) => (
                    <label key={tool} className={styles.toolOption}>
                      <input type="checkbox" checked={hasTool(service, tool)} onChange={() => toggleTool(service, tool)} />
                      {label}
                    </label>
                  ))}
                </div>
              ) : null}
              {serviceNote ? <p className={`${styles.hint} ${styles.rowNote}`}>{serviceNote}</p> : null}
              {rowError ? <p className={styles.error}>{rowError}</p> : null}
            </li>
          )
        })}
      </ol>

      {errors.services ? <p className={styles.error}>{errors.services}</p> : null}

      <div>
        <Button variant="secondary" size="sm" onClick={addService} disabled={value.length >= limits.maxServicesPerHost}>
          Add service
        </Button>
      </div>
    </fieldset>
  )
}
