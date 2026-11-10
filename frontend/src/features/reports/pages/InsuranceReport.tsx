import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Spinner } from '../../../components/ui/Spinner'
import { TextArea } from '../../../components/ui/TextArea'
import { TextField } from '../../../components/ui/TextField'
import { toFormErrors } from '../../../lib/formErrors'
import type { SurveyFact, ToolSection } from '../../databases/databaseApi'
import { hostsApi } from '../../inventory/api/inventoryApi'
import { credentialProblem, usedWith } from '../../inventory/hostMenu'
import { useResource } from '../../inventory/hooks/useResource'
import { withInheritedTags } from '../../inventory/inheritance'
import { formatAccount, type Host, type HostCredential } from '../../inventory/types'
import type { CertificatePrompt } from '../../terminal/kvm'
import { downloadQuestionnaire, questionnaireHtml, type DocumentInput } from '../insurance/document'
import { answersApi, surveyInstance, type SavedAnswers } from '../insurance/insuranceApi'
import {
  BACKUP_QUESTIONS,
  businessDatabases,
  countedHosts,
  DATABASE_QUESTIONS,
  databaseInstances,
  factsFor,
  newRepositoryId,
  serverFacts,
  summarizeServers,
  type Answers,
  type DatabaseInstance,
  type ExtraRepository,
  type InstanceAnswers,
  type QuestionnaireAnswers,
  type Question,
} from '../insurance/model'
import styles from './Insurance.module.css'
import reportStyles from './Reports.module.css'

const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** Where a server's survey is at: running, waiting for a certificate to be trusted, or failed. */
type SurveyState = { running: true } | { running: false; error?: string; trust?: { certificate: CertificatePrompt; renewed: boolean; credentialId: string } }

/**
 * The insurance questionnaire: the number and kinds of servers, each database (its software, who manages it,
 * what it holds, who can change it, where it is, deletions and change logs, documentation) and each
 * repository's backups. NKeeper fills in what it knows from the inventory and from read-only surveys of the
 * database servers, which a person starts; people type the rest. Answers are saved for everyone, and the whole
 * thing downloads as a Word document or a web page to print.
 */
export function InsuranceReport() {
  const hosts = useResource((signal) => hostsApi.list(signal))
  const saved = useResource((signal) => answersApi.get(signal))

  const error = hosts.error ?? saved.error
  if (error && (!hosts.data || !saved.data)) {
    return (
      <div className={reportStyles.state}>
        <Alert>{toFormErrors(error).message}</Alert>
        <Button
          variant="secondary"
          size="sm"
          onClick={() => {
            hosts.reload()
            saved.reload()
          }}
        >
          Try again
        </Button>
      </div>
    )
  }
  if (!hosts.data || !saved.data) {
    return (
      <div className={reportStyles.state}>
        <Spinner label="Loading the questionnaire" />
      </div>
    )
  }
  // The saved answers are read once; after that the page's own copy is the one edited.
  return <Questionnaire hosts={hosts.data} saved={saved.data} />
}

/** Downloads the questionnaire as it stands, dated today. */
function downloadNow(input: Omit<DocumentInput, 'generatedAt'>, kind: 'word' | 'html') {
  const now = new Date()
  downloadQuestionnaire(questionnaireHtml({ ...input, generatedAt: now }), `insurance-questionnaire-${now.toISOString().slice(0, 10)}`, kind)
}

function Questionnaire({ hosts, saved }: { hosts: Host[]; saved: SavedAnswers }) {
  const [answers, setAnswersState] = useState<QuestionnaireAnswers>(() => saved.answers ?? {})
  const [dirty, setDirty] = useState(false)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [savedInfo, setSavedInfo] = useState<{ at: string | null; by: string | null }>({ at: saved.updatedAtUtc, by: saved.updatedBy })
  const [sections, setSections] = useState<Record<string, ToolSection[]>>({})
  const [surveys, setSurveys] = useState<Record<string, SurveyState>>({})
  const [readingAll, setReadingAll] = useState(false)

  useEffect(() => {
    if (!dirty) {
      return
    }
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const update = useCallback((change: (current: QuestionnaireAnswers) => QuestionnaireAnswers) => {
    setAnswersState((current) => change(current))
    setDirty(true)
  }, [])

  const allHosts = useMemo(() => withInheritedTags(hosts), [hosts])
  const counted = useMemo(() => countedHosts(allHosts, answers.excludeHosts), [allHosts, answers.excludeHosts])
  const servers = useMemo(() => summarizeServers(counted), [counted])
  const instances = useMemo(() => databaseInstances(allHosts), [allHosts])

  const setInstance = (key: string, change: (current: InstanceAnswers) => InstanceAnswers) =>
    update((a) => ({ ...a, instances: { ...a.instances, [key]: change(a.instances?.[key] ?? {}) } }))

  const included = instances.filter((i) => !answers.instances?.[i.key]?.excluded)

  const save = async () => {
    setSaving(true)
    setSaveError(null)
    try {
      const result = await answersApi.save(answers)
      setSavedInfo({ at: result.updatedAtUtc, by: result.updatedBy })
      setDirty(false)
    } catch (e) {
      setSaveError(toFormErrors(e).message)
    } finally {
      setSaving(false)
    }
  }

  const read = async (instance: DatabaseInstance, credentialId: string, trustFingerprint?: string) => {
    setSurveys((s) => ({ ...s, [instance.key]: { running: true } }))
    try {
      const outcome = await surveyInstance(instance, credentialId, trustFingerprint)
      if (outcome.status === 'trust') {
        setSurveys((s) => ({ ...s, [instance.key]: { running: false, trust: { certificate: outcome.certificate, renewed: outcome.renewed, credentialId } } }))
        return
      }
      const { sections: found, ...survey } = outcome.survey
      setSections((s) => ({ ...s, [instance.key]: found }))
      setInstance(instance.key, (current) => ({ ...current, survey, credentialId }))
      setSurveys((s) => ({ ...s, [instance.key]: { running: false } }))
    } catch (e) {
      setSurveys((s) => ({ ...s, [instance.key]: { running: false, error: toFormErrors(e).message || 'The server couldn’t be read.' } }))
    }
  }

  const readAll = async () => {
    setReadingAll(true)
    try {
      for (const instance of included) {
        const credential = chosenCredential(instance, answers.instances?.[instance.key])
        if (credential) {
          await read(instance, credential.id)
        }
      }
    } finally {
      setReadingAll(false)
    }
  }

  const download = (kind: 'word' | 'html') => downloadNow({ answers, servers, instances: included, sections }, kind)

  const repositories = answers.repositories ?? []
  const setRepository = (id: string, change: (r: ExtraRepository) => ExtraRepository) =>
    update((a) => ({ ...a, repositories: (a.repositories ?? []).map((r) => (r.id === id ? change(r) : r)) }))
  const readable = included.filter((i) => chosenCredential(i, answers.instances?.[i.key])).length

  return (
    <>
      <header className={reportStyles.reportHeader}>
        <div>
          <h2 className={reportStyles.reportTitle}>Insurance questionnaire</h2>
          <p className={reportStyles.reportSummary}>
            {dirty
              ? 'Unsaved changes'
              : savedInfo.at
                ? `Saved ${timeFormat.format(new Date(savedInfo.at))}${savedInfo.by ? ` by ${savedInfo.by}` : ''}`
                : 'Not saved yet'}
          </p>
        </div>
        <div className={reportStyles.reportActions}>
          <Button size="sm" onClick={save} loading={saving} disabled={!dirty}>
            Save answers
          </Button>
          <Button size="sm" variant="secondary" onClick={() => download('word')}>
            Download Word
          </Button>
          <Button size="sm" variant="secondary" onClick={() => download('html')}>
            Download HTML
          </Button>
        </div>
      </header>

      <div className={styles.body}>
        {saveError ? <Alert>{saveError}</Alert> : null}
        <p className={styles.intro}>
          The questions an insurer (or an e-discovery request) asks about servers, databases and backups. NKeeper fills in what it knows from its
          inventory and, when you read a database server, from read-only queries of it. It never guesses what only you know (what a database is for,
          who manages it, documentation): those are left blank in the download for you to fill in, unless you answer them here. Answers typed here
          are saved for everyone and kept for next time. The download is a Word document (or a web page to print to PDF).
        </p>

        <Section title="About the report">
          <div className={styles.twoColumns}>
            <TextField label="Organisation" value={answers.organisation ?? ''} onChange={(e) => update((a) => ({ ...a, organisation: e.target.value }))} />
            <TextField label="Prepared by" value={answers.preparedBy ?? ''} onChange={(e) => update((a) => ({ ...a, preparedBy: e.target.value }))} />
          </div>
          <TextArea
            label="Introduction (optional)"
            rows={2}
            value={answers.introduction ?? ''}
            onChange={(e) => update((a) => ({ ...a, introduction: e.target.value }))}
          />
          <Checkbox
            label="Add an appendix with what each database server reported (from servers read since this page was opened)"
            checked={answers.includeAppendix !== false}
            onChange={(e) => update((a) => ({ ...a, includeAppendix: e.target.checked }))}
          />
        </Section>

        <Section title="1. Servers" summary={`${servers.total} servers: ${servers.physical} physical, ${servers.virtual} virtual`}>
          <TextField
            label="Don’t count hosts matching"
            hint="Searches as in the workspace, separated by commas, e.g. role:printer, role:switch, env:lab"
            value={answers.excludeHosts ?? ''}
            onChange={(e) => update((a) => ({ ...a, excludeHosts: e.target.value }))}
          />
          <div className={styles.counts}>
            <CountTable title="By type" rows={[{ label: 'Physical', count: servers.physical }, { label: 'Virtual machine', count: servers.virtual }]} />
            <CountTable title="By operating system" rows={servers.byOs} />
            {servers.byService.length > 0 ? <CountTable title="By what they run" rows={servers.byService} /> : null}
            {servers.byTag.map((t) => (
              <CountTable key={t.tag} title={`By ${t.tag}`} rows={t.rows} />
            ))}
          </div>
          <p className={styles.muted}>
            The download lists every counted server with its address, operating system, location, CPUs, memory and serial number.
            {allHosts.length > counted.length ? ` ${allHosts.length - counted.length} hosts aren’t counted.` : ''}
          </p>
          <TextArea label="Notes on servers (optional)" rows={2} value={answers.serverNotes ?? ''} onChange={(e) => update((a) => ({ ...a, serverNotes: e.target.value }))} />
        </Section>

        <Section title="2. Databases" summary={`${included.length} database server${included.length === 1 ? '' : 's'}`}>
          <div className={styles.actions}>
            <Button size="sm" onClick={readAll} loading={readingAll} disabled={readable === 0}>
              Read every database server
            </Button>
            <span className={styles.muted}>
              {readable === included.length
                ? 'Reads each server with its saved login, one at a time. It only reads; nothing is changed.'
                : `${included.length - readable} server${included.length - readable === 1 ? ' has' : 's have'} no saved login to read it with.`}
            </span>
          </div>
          <details className={styles.card}>
            <summary>Answers for every database (unless a server or database has its own)</summary>
            <QuestionList
              questions={DATABASE_QUESTIONS}
              answers={answers.databaseDefaults}
              onChange={(key, value) => update((a) => ({ ...a, databaseDefaults: { ...a.databaseDefaults, [key]: value } }))}
            />
          </details>
          {instances.length === 0 ? <p className={styles.muted}>NKeeper has no SQL Server, Oracle or SAP ASE services in its inventory.</p> : null}
          {instances.map((instance) => (
            <InstanceCard
              key={instance.key}
              instance={instance}
              saved={answers.instances?.[instance.key]}
              defaults={answers.databaseDefaults}
              state={surveys[instance.key]}
              onRead={(credentialId, fingerprint) => read(instance, credentialId, fingerprint)}
              onChange={(change) => setInstance(instance.key, change)}
            />
          ))}
        </Section>

        <Section title="3. Backups" summary={`${included.length + repositories.length} repositories`}>
          <p className={styles.muted}>
            Each database server is a repository; add the others (file servers, e-mail, Microsoft 365, accounting systems…). What’s the same everywhere
            (the backup software, where tapes go) can be answered once for all of them.
          </p>
          <details className={styles.card}>
            <summary>Answers for every repository (unless it has its own)</summary>
            <QuestionList
              questions={BACKUP_QUESTIONS}
              answers={answers.backupDefaults}
              onChange={(key, value) => update((a) => ({ ...a, backupDefaults: { ...a.backupDefaults, [key]: value } }))}
            />
          </details>
          {repositories.map((repository) => (
            <details key={repository.id} className={styles.card} open={!repository.name}>
              <summary>{repository.name || 'New repository'}</summary>
              <div className={styles.repositoryName}>
                <TextField
                  label="Repository"
                  hint="e.g. File server FS01, Microsoft 365 e-mail, Accounting (QuickBooks)"
                  value={repository.name}
                  onChange={(e) => setRepository(repository.id, (r) => ({ ...r, name: e.target.value }))}
                />
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => update((a) => ({ ...a, repositories: (a.repositories ?? []).filter((r) => r.id !== repository.id) }))}
                >
                  Remove
                </Button>
              </div>
              <QuestionList
                questions={BACKUP_QUESTIONS}
                answers={repository.answers}
                defaults={answers.backupDefaults}
                onChange={(key, value) => setRepository(repository.id, (r) => ({ ...r, answers: { ...r.answers, [key]: value } }))}
              />
            </details>
          ))}
          <div className={styles.actions}>
            <Button
              size="sm"
              variant="secondary"
              onClick={() => update((a) => ({ ...a, repositories: [...(a.repositories ?? []), { id: newRepositoryId(), name: '', answers: {} }] }))}
            >
              Add a repository
            </Button>
          </div>
          {included.map((instance) => {
            const own = answers.instances?.[instance.key]
            return (
              <details key={instance.key} className={styles.card}>
                <summary>
                  {instance.title}
                  <span className={styles.status}>{own?.survey ? `read ${timeFormat.format(new Date(own.survey.atUtc))}` : 'not read'}</span>
                </summary>
                <QuestionList
                  questions={BACKUP_QUESTIONS}
                  answers={own?.backup}
                  defaults={answers.backupDefaults}
                  facts={(q) => factsFor(own?.survey, q.topics)}
                  onChange={(key, value) => setInstance(instance.key, (i) => ({ ...i, backup: { ...i.backup, [key]: value } }))}
                />
              </details>
            )
          })}
        </Section>
      </div>
    </>
  )
}

/** The credential to read a server with: the one used last, or the first meant for its service. */
function chosenCredential(instance: DatabaseInstance, saved: InstanceAnswers | undefined): HostCredential | undefined {
  const usable = usableCredentials(instance)
  return usable.find((c) => c.id === saved?.credentialId) ?? usable[0]
}

function usableCredentials(instance: DatabaseInstance): HostCredential[] {
  return instance.host.credentials.filter((c) => c.hasPassword && usedWith(c, instance.service) && !credentialProblem(c, instance.service.protocol))
}

function Section({ title, summary, children }: { title: string; summary?: string; children: ReactNode }) {
  return (
    <section className={styles.section}>
      <h3 className={styles.sectionTitle}>
        {title}
        {summary ? <span className={styles.status}>{summary}</span> : null}
      </h3>
      {children}
    </section>
  )
}

function CountTable({ title, rows }: { title: string; rows: { label: string; count: number }[] }) {
  return (
    <table className={styles.countTable}>
      <caption>{title}</caption>
      <tbody>
        {rows.map((row) => (
          <tr key={row.label}>
            <th scope="row">{row.label}</th>
            <td>{row.count}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

interface InstanceCardProps {
  instance: DatabaseInstance
  saved: InstanceAnswers | undefined
  defaults: Answers<(typeof DATABASE_QUESTIONS)[number]['key']> | undefined
  state: SurveyState | undefined
  onRead: (credentialId: string, trustFingerprint?: string) => void
  onChange: (change: (current: InstanceAnswers) => InstanceAnswers) => void
}

/** A database server: reading it, its answers, and its databases'. */
function InstanceCard({ instance, saved, defaults, state, onRead, onChange }: InstanceCardProps) {
  const credentials = usableCredentials(instance)
  const [credentialId, setCredentialId] = useState(() => chosenCredential(instance, saved)?.id ?? '')
  const survey = saved?.survey
  const databases = businessDatabases(survey)
  const running = state?.running === true
  const failed = state && !state.running ? state : undefined

  return (
    <details className={`${styles.card} ${saved?.excluded ? styles.excluded : ''}`}>
      <summary>
        {instance.title}
        <span className={styles.status}>
          {saved?.excluded
            ? 'left out'
            : running
              ? 'reading…'
              : survey
                ? `${survey.product} ${survey.version ?? ''} · ${databases.length} database${databases.length === 1 ? '' : 's'} · read ${timeFormat.format(new Date(survey.atUtc))}`
                : 'not read'}
        </span>
      </summary>
      <p className={styles.muted}>{instance.location}</p>
      <div className={styles.actions}>
        {credentials.length > 0 ? (
          <>
            <label className={styles.inlineLabel}>
              Login
              <select value={credentialId} onChange={(e) => setCredentialId(e.target.value)} disabled={running}>
                {credentials.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.label ? `${c.label} (${formatAccount(c)})` : formatAccount(c)}
                  </option>
                ))}
              </select>
            </label>
            <Button size="sm" variant="secondary" onClick={() => onRead(credentialId)} loading={running} disabled={!credentialId || saved?.excluded}>
              {survey ? 'Read again' : 'Read from the server'}
            </Button>
          </>
        ) : (
          <span className={styles.muted}>Add a login with a password for this service to the host to read it.</span>
        )}
        <Checkbox label="Leave out of the report" checked={!!saved?.excluded} onChange={(e) => onChange((i) => ({ ...i, excluded: e.target.checked }))} />
      </div>
      {failed?.error ? <Alert>{failed.error}</Alert> : null}
      {failed?.trust ? (
        <Alert variant="warning">
          <p>
            {failed.trust.renewed ? 'The server has a new certificate' : 'The server’s certificate hasn’t been trusted yet'}: {failed.trust.certificate.subject}, expires{' '}
            {failed.trust.certificate.expires}.
          </p>
          <p className={styles.fingerprint}>SHA-256 {failed.trust.certificate.fingerprint}</p>
          <Button size="sm" onClick={() => onRead(failed.trust!.credentialId, failed.trust!.certificate.fingerprint)}>
            Trust it and read
          </Button>
        </Alert>
      ) : null}

      <h4 className={styles.subTitle}>The server (its answers apply to each of its databases unless one has its own)</h4>
      <QuestionList
        questions={DATABASE_QUESTIONS}
        answers={saved?.server}
        defaults={defaults}
        facts={(q) => serverFacts(survey, q.topics)}
        onChange={(key, value) => onChange((i) => ({ ...i, server: { ...i.server, [key]: value } }))}
      />

      {databases.map((database) => (
        <details key={database.name} className={styles.innerCard}>
          <summary>
            Database {database.name}
            <span className={styles.status}>
              {[database.sizeMb == null ? null : `${Math.round(database.sizeMb).toLocaleString()} MB`, database.created ? `created ${database.created}` : null]
                .filter(Boolean)
                .join(' · ')}
            </span>
          </summary>
          <QuestionList
            questions={DATABASE_QUESTIONS}
            answers={saved?.databases?.[database.name]}
            placeholder="Same as the server’s answer"
            facts={(q) => factsFor(survey, q.topics, database.name).filter((f) => f.database != null)}
            onChange={(key, value) =>
              onChange((i) => ({ ...i, databases: { ...i.databases, [database.name]: { ...i.databases?.[database.name], [key]: value } } }))
            }
          />
        </details>
      ))}
    </details>
  )
}

interface QuestionListProps<K extends string> {
  questions: Question<K>[]
  answers: Answers<K> | undefined
  /** The answer that applies when this one is empty, shown as its placeholder. */
  defaults?: Answers<K>
  placeholder?: string
  facts?: (question: Question<K>) => SurveyFact[]
  onChange: (key: K, value: string) => void
}

function QuestionList<K extends string>({ questions, answers, defaults, placeholder, facts, onChange }: QuestionListProps<K>) {
  return (
    <div className={styles.questions}>
      {questions.map((q) => {
        const found = facts?.(q) ?? []
        const value = answers?.[q.key] ?? ''
        const fallback = defaults?.[q.key]?.trim()
        return (
          <div key={q.key} className={styles.question}>
            <TextArea
              label={q.label}
              hint={q.hint}
              rows={2}
              value={value}
              placeholder={fallback ? `Same as for all: ${fallback}` : placeholder}
              onChange={(e) => onChange(q.key, e.target.value)}
            />
            {found.length > 0 ? (
              <div className={styles.found}>
                <div className={styles.foundHeader}>
                  <span>Found by NKeeper (goes in the report under the answer)</span>
                  {!value.trim() ? (
                    <Button size="sm" variant="ghost" onClick={() => onChange(q.key, found.map((f) => (f.database ? `${f.database}: ${f.text}` : f.text)).join('\n'))}>
                      Copy into the answer
                    </Button>
                  ) : null}
                </div>
                <ul>
                  {found.map((f, i) => (
                    <li key={i}>
                      {f.database ? <b>{f.database}: </b> : null}
                      {f.text}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
          </div>
        )
      })}
    </div>
  )
}
