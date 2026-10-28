import { useState, type FormEvent } from 'react'
import { Alert, Button, Checkbox, PasswordField, Spinner, styles as nkeeper, TextField, toFormErrors, useResource } from '@nkeeper/plugin-sdk'
import { zabbixApi, type ZabbixCertificate, type ZabbixSettings, type ZabbixSettingsInput } from '../zabbixApi'

const styles = nkeeper.page
const own = nkeeper.settings
const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

const FIELDS = ['enabled', 'url', 'apiToken']

/** Settings ▸ Zabbix: loads the saved settings, then shows the form for them. */
export function ZabbixSettingsSection() {
  const zabbix = useResource((signal) => zabbixApi.getSettings(signal))
  if (zabbix.error && !zabbix.data) {
    return <Alert>{toFormErrors(zabbix.error).message}</Alert>
  }
  return zabbix.data ? <ZabbixSettingsPanel key={zabbix.data.updatedAtUtc ?? 'new'} saved={zabbix.data} onSaved={zabbix.reload} /> : <Spinner label="Loading" />
}

/** The Zabbix server NKeeper reads host information from, with an API token. */
export function ZabbixSettingsPanel({ saved, onSaved }: { saved: ZabbixSettings; onSaved: () => void }) {
  const [enabled, setEnabled] = useState(saved.enabled)
  const [url, setUrl] = useState(saved.url)
  const [apiToken, setApiToken] = useState('')
  // A certificate trusted in this form, sent with the next test and save.
  const [trustFingerprint, setTrustFingerprint] = useState<string | null>(null)
  const [certificate, setCertificate] = useState<ZabbixCertificate | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<{ kind: 'info' | 'error'; text: string } | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | null>(null)

  function input(trust = trustFingerprint): ZabbixSettingsInput {
    return { enabled, url: url.trim(), apiToken: apiToken.trim() || null, trustFingerprint: trust }
  }

  async function run(kind: 'save' | 'test', trust = trustFingerprint) {
    setMessage(null)
    setErrors({})
    setCertificate(null)
    setBusy(kind)
    try {
      if (kind === 'save') {
        await zabbixApi.saveSettings(input(trust))
        // Remounts with the saved values (the key is the update time).
        onSaved()
        return
      }
      const result = await zabbixApi.testSettings(input(trust))
      if (result.status === 'trustRequired') {
        setCertificate(result.certificate)
      } else {
        setMessage({
          kind: 'info',
          text: `Connected to Zabbix ${result.version}. The API token can see ${result.hostCount} host${result.hostCount === 1 ? '' : 's'}.${
            trust && trust !== saved.trustedCertificateFingerprint ? ' Save to keep the trusted certificate.' : ''
          }`,
        })
      }
    } catch (error) {
      const { fields, message: text } = toFormErrors(error, FIELDS)
      setErrors(fields)
      setMessage({ kind: 'error', text: text ?? fields.url ?? fields.apiToken ?? 'Please check the highlighted fields.' })
    } finally {
      setBusy(null)
    }
  }

  function trust(fingerprint: string) {
    setTrustFingerprint(fingerprint)
    void run('test', fingerprint)
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    void run('save')
  }

  return (
    <section className={styles.panel} aria-labelledby="zabbix-title">
      <div>
        <h2 id="zabbix-title" className={styles.panelTitle}>
          Zabbix
        </h2>
        <p className={styles.hint}>
          The Zabbix server (7.0 or later) NKeeper reads host information from: status, inventory, latest values and problems.
          NKeeper only reads from Zabbix. Create an API token in Zabbix under User settings ▸ API tokens, for a user that can
          read the hosts.
          {saved.updatedAtUtc ? ` Last saved ${dateTimeFormat.format(new Date(saved.updatedAtUtc))}.` : ' Not set up yet.'}
        </p>
      </div>
      {message ? <Alert variant={message.kind}>{message.text}</Alert> : null}
      {certificate ? (
        <Alert variant="info">
          <p>
            The Zabbix server’s certificate isn’t publicly trusted. Trust it only if the fingerprint matches the server’s
            certificate.
          </p>
          <dl className={own.certificate}>
            <dt>Subject</dt>
            <dd>{certificate.subject}</dd>
            <dt>Issuer</dt>
            <dd>{certificate.issuer}</dd>
            <dt>Expires</dt>
            <dd>{dateFormat.format(new Date(certificate.expires))}</dd>
            <dt>SHA-256</dt>
            <dd className={own.fingerprint}>{certificate.fingerprint}</dd>
          </dl>
          <div className={own.actions}>
            <Button size="sm" onClick={() => trust(certificate.fingerprint)}>
              Trust and test
            </Button>
            <Button variant="ghost" size="sm" onClick={() => setCertificate(null)}>
              Don’t trust
            </Button>
          </div>
        </Alert>
      ) : null}
      <form className={styles.form} onSubmit={handleSubmit} noValidate>
        <Checkbox label="Read host information from Zabbix" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        <TextField
          label="Zabbix address"
          value={url}
          onChange={(e) => {
            setUrl(e.target.value)
            setTrustFingerprint(null)
          }}
          error={errors.url}
          placeholder="https://zabbix.example.com/zabbix"
          hint="The address you open Zabbix at. Use HTTPS: the API token is sent with every request."
          maxLength={2048}
          autoComplete="off"
          spellCheck={false}
        />
        <PasswordField
          label="API token"
          value={apiToken}
          onChange={(e) => setApiToken(e.target.value)}
          error={errors.apiToken}
          hint={saved.hasApiToken ? 'A token is saved. Leave this empty to keep it.' : undefined}
          autoComplete="new-password"
          maxLength={512}
        />
        {saved.trustedCertificateFingerprint ? (
          <p className={styles.hint}>
            Trusted certificate: <span className={own.fingerprint}>{saved.trustedCertificateFingerprint}</span>
          </p>
        ) : null}
        <div className={own.actions}>
          <Button type="submit" loading={busy === 'save'} disabled={busy !== null}>
            Save
          </Button>
          <Button variant="secondary" loading={busy === 'test'} disabled={busy !== null || !url.trim()} onClick={() => void run('test')}>
            Test connection
          </Button>
        </div>
      </form>
    </section>
  )
}
