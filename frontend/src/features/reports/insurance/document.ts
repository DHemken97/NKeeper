import type { SurveyFact, ToolSection } from '../../databases/databaseApi'
import {
  BACKUP_QUESTIONS,
  businessDatabases,
  DATABASE_QUESTIONS,
  factsFor,
  resolve,
  serverFacts,
  type Answers,
  type DatabaseInstance,
  type ExtraRepository,
  type InstanceAnswers,
  type QuestionnaireAnswers,
  type ServerSummary,
} from './model'

/** Rows of a result shown in the appendix; the rest are counted. */
const APPENDIX_ROWS = 200

export interface DocumentInput {
  answers: QuestionnaireAnswers
  servers: ServerSummary
  /** The database servers in the report (not left out). */
  instances: DatabaseInstance[]
  /** Each surveyed server's sections, by instance key, for the appendix (only surveys run since the page opened have them). */
  sections: Record<string, ToolSection[]>
  generatedAt: Date
}

const dateFormat = new Intl.DateTimeFormat('en-US', { dateStyle: 'long' })
const timeFormat = new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' })

export function escapeHtml(text: string | null | undefined): string {
  return (text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;')
}

/** Typed text with its line breaks kept. */
function paragraphs(text: string): string {
  return escapeHtml(text).replaceAll(/\r?\n/g, '<br>')
}

function table(headers: string[], rows: (string | number | null | undefined)[][], className = ''): string {
  if (rows.length === 0) {
    return '<p class="muted">None.</p>'
  }
  return `<table class="${className}"><thead><tr>${headers.map((h) => `<th>${escapeHtml(h)}</th>`).join('')}</tr></thead><tbody>${rows
    .map((row) => `<tr>${row.map((cell) => `<td>${escapeHtml(cell == null ? '' : String(cell))}</td>`).join('')}</tr>`)
    .join('')}</tbody></table>`
}

function factList(facts: SurveyFact[], prefixDatabase = false): string {
  if (facts.length === 0) {
    return ''
  }
  const items = facts.map((f) => `<li>${prefixDatabase && f.database ? `<b>${escapeHtml(f.database)}:</b> ` : ''}${escapeHtml(f.text)}</li>`)
  return `<div class="found"><span class="foundTitle">Found by NKeeper:</span><ul>${items.join('')}</ul></div>`
}

/** One question and its answer, with what NKeeper found beneath. */
function qa(label: string, answer: string, allFacts: SurveyFact[], prefixDatabase = false): string {
  // Facts copied into the answer aren't repeated under it.
  const facts = allFacts.filter((f) => !answer.includes(f.text))
  // Nothing is guessed: an unanswered question is a blank space to fill in after downloading.
  const typed = answer ? `<div>${paragraphs(answer)}</div>` : '<p class="blank">&nbsp;</p>'
  return `<tr><th class="question">${escapeHtml(label)}</th><td>${typed}${factList(facts, prefixDatabase)}</td></tr>`
}

function serversChapter(input: DocumentInput): string {
  const { servers, answers } = input
  const parts = [
    '<h2>1. Servers</h2>',
    `<p><b>${servers.total}</b> servers: <b>${servers.physical}</b> physical and <b>${servers.virtual}</b> virtual machines${
      servers.hypervisors > 0 ? `; ${servers.hypervisors} of the physical servers host virtual machines` : ''
    }.</p>`,
  ]
  if (answers.excludeHosts?.trim()) {
    parts.push(`<p class="muted">Not counted: hosts matching ${escapeHtml(answers.excludeHosts.trim())}.</p>`)
  }
  if (answers.serverNotes?.trim()) {
    parts.push(`<p>${paragraphs(answers.serverNotes.trim())}</p>`)
  }
  parts.push('<h3>By type</h3>', table(['Type', 'Servers'], [['Physical', servers.physical], ['Virtual machine', servers.virtual]], 'narrow'))
  parts.push('<h3>By operating system</h3>', table(['Operating system', 'Servers'], servers.byOs.map((r) => [r.label, r.count]), 'narrow'))
  if (servers.byService.length > 0) {
    parts.push(
      '<h3>By what they run</h3>',
      '<p class="muted">A server is counted under each service it offers.</p>',
      table(['Kind', 'Servers'], servers.byService.map((r) => [r.label, r.count]), 'narrow'),
    )
  }
  servers.byTag.forEach(({ tag, rows }) => {
    parts.push(`<h3>By ${escapeHtml(tag)}</h3>`, table([tag[0].toUpperCase() + tag.slice(1), 'Servers'], rows.map((r) => [r.label, r.count]), 'narrow'))
  })
  parts.push(
    '<h3>All servers</h3>',
    table(
      ['Name', 'Address', 'Type', 'Operating system', 'Location', 'CPUs', 'Memory', 'Serial number', 'Services'],
      servers.rows.map((r) => [r.name, r.address, r.kind, r.os, r.location, r.cpu, r.memory, r.serial, r.services]),
      'small',
    ),
  )
  return parts.join('\n')
}

/** The answer to show for a database question, falling back to what the inventory or survey knows for name, software and location. */
function databaseAnswer(
  key: (typeof DATABASE_QUESTIONS)[number]['key'],
  saved: InstanceAnswers | undefined,
  defaults: Answers<(typeof DATABASE_QUESTIONS)[number]['key']> | undefined,
  database?: string,
): string {
  const own = database ? saved?.databases?.[database] : undefined
  return resolve(key, own, saved?.server, defaults)
}

function databasesChapter(input: DocumentInput): string {
  const { instances, answers } = input
  const parts = ['<h2>2. Databases</h2>']
  if (instances.length === 0) {
    parts.push('<p>NKeeper has no database servers in its inventory.</p>')
    return parts.join('\n')
  }

  const summaryRows = instances.flatMap((instance) => {
    const saved = answers.instances?.[instance.key]
    const survey = saved?.survey
    const software = survey ? `${survey.product} ${survey.version ?? ''}`.trim() : instance.product
    const dbs = businessDatabases(survey)
    if (dbs.length === 0) {
      return [[survey ? '(no business databases found)' : '(not read from the server)', instance.host.name, software, '', '']]
    }
    return dbs.map((d) => [d.name, instance.host.name, software, d.sizeMb == null ? '' : `${Math.round(d.sizeMb).toLocaleString('en-US')} MB`, d.created ?? ''])
  })
  parts.push('<h3>Summary</h3>', table(['Database', 'Server', 'Software', 'Size', 'Created'], summaryRows))

  instances.forEach((instance, i) => {
    const saved = answers.instances?.[instance.key]
    const survey = saved?.survey
    parts.push(`<h3>2.${i + 1} ${escapeHtml(instance.title)}</h3>`)
    parts.push(
      `<p class="muted">${
        survey
          ? `Read from the server on ${escapeHtml(timeFormat.format(new Date(survey.atUtc)))}: ${escapeHtml(survey.product)} ${escapeHtml(survey.version)}, ${escapeHtml(survey.developer)}.`
          : 'Not read from the server: the answers are as typed.'
      } Location: ${escapeHtml(instance.location)}.</p>`,
    )
    parts.push('<table class="qa"><tbody>')
    DATABASE_QUESTIONS.forEach((q) => {
      const answer = databaseAnswer(q.key, saved, answers.databaseDefaults)
      // What the inventory knows is shown as found, never written in as the answer.
      const inventory: SurveyFact[] =
        q.key === 'location'
          ? [{ topic: 'location', text: `In NKeeper's inventory: ${instance.location}.` }]
          : q.key === 'software' && !survey
            ? [{ topic: 'software', text: `In NKeeper's inventory as a ${instance.product} service (not read from the server, so its version isn't known).` }]
            : []
      parts.push(qa(q.label, answer, [...inventory, ...serverFacts(survey, q.topics)]))
    })
    parts.push('</tbody></table>')

    businessDatabases(survey).forEach((database) => {
      const own = saved?.databases?.[database.name]
      const dbFacts = (topics: (typeof DATABASE_QUESTIONS)[number]['topics']) =>
        factsFor(survey, topics, database.name).filter((f) => f.database?.toLowerCase() === database.name.toLowerCase())
      const rows = DATABASE_QUESTIONS.map((q) => ({ q, answer: own?.[q.key]?.trim() ?? '', facts: dbFacts(q.topics) })).filter(
        (r) => r.answer || r.facts.length > 0,
      )
      parts.push(`<h4>Database ${escapeHtml(database.name)}</h4>`)
      parts.push(
        `<p class="muted">${[
          database.sizeMb == null ? null : `${Math.round(database.sizeMb).toLocaleString('en-US')} MB`,
          database.created ? `created ${database.created}` : null,
          database.owner ? `owner ${database.owner}` : null,
          database.location ? `stored in ${database.location}` : null,
        ]
          .filter(Boolean)
          .map((s) => escapeHtml(s))
          .join(' · ')}. Questions not listed have the server’s answers above.</p>`,
      )
      if (rows.length > 0) {
        parts.push(`<table class="qa"><tbody>${rows.map((r) => qa(r.q.label, r.answer, r.facts)).join('')}</tbody></table>`)
      }
    })
  })
  return parts.join('\n')
}

function backupChapter(input: DocumentInput): string {
  const { instances, answers } = input
  const parts = ['<h2>3. Backups, for each repository</h2>']
  const repositories: { title: string; detail: string; own: Answers<(typeof BACKUP_QUESTIONS)[number]['key']> | undefined; facts?: InstanceAnswers['survey'] }[] = [
    ...(answers.repositories ?? []).map((r: ExtraRepository) => ({ title: r.name || 'Unnamed repository', detail: '', own: r.answers })),
    ...instances.map((instance) => ({
      title: instance.title,
      detail: instance.location,
      own: answers.instances?.[instance.key]?.backup,
      facts: answers.instances?.[instance.key]?.survey,
    })),
  ]
  if (repositories.length === 0) {
    parts.push('<p>No repositories were described.</p>')
  }
  repositories.forEach((repository, i) => {
    parts.push(`<h3>3.${i + 1} ${escapeHtml(repository.title)}</h3>`)
    if (repository.detail) {
      parts.push(`<p class="muted">${escapeHtml(repository.detail)}</p>`)
    }
    parts.push(
      `<table class="qa"><tbody>${BACKUP_QUESTIONS.map((q) => qa(q.label, resolve(q.key, repository.own, answers.backupDefaults), factsFor(repository.facts, q.topics), true)).join('')}</tbody></table>`,
    )
  })
  return parts.join('\n')
}

function appendixChapter(input: DocumentInput): string {
  const surveyed = input.instances.filter((i) => (input.sections[i.key] ?? []).length > 0)
  if (surveyed.length === 0) {
    return ''
  }
  const parts = ['<h2 class="pageBreak">Appendix: what the database servers reported</h2>', '<p class="muted">The read-only queries NKeeper ran, and their results.</p>']
  surveyed.forEach((instance) => {
    parts.push(`<h3>${escapeHtml(instance.title)}</h3>`)
    input.sections[instance.key].forEach((section) => {
      parts.push(`<h4>${escapeHtml(section.title)}</h4>`, `<p class="muted">From ${escapeHtml(section.command)}${section.note ? `. ${escapeHtml(section.note)}` : ''}</p>`)
      if (section.error) {
        parts.push(`<p class="muted">Couldn’t be read: ${escapeHtml(section.error)}</p>`)
      }
      section.results.forEach((result) => {
        if (result.kind === 'results' && result.columns && result.rows) {
          const shown = result.rows.slice(0, APPENDIX_ROWS)
          parts.push(table(result.columns.map((c) => c.name), shown, 'small'))
          const total = result.rowCount ?? result.rows.length
          if (total > shown.length) {
            parts.push(`<p class="muted">${total - shown.length} more rows not shown.</p>`)
          }
        } else if (result.kind === 'message' && result.text) {
          parts.push(`<p class="muted">${escapeHtml(result.text)}</p>`)
        }
      })
    })
  })
  return parts.join('\n')
}

const STYLE = `
  body { font-family: Calibri, Arial, sans-serif; font-size: 10.5pt; color: #111; margin: 2em; }
  h1 { font-size: 20pt; margin: 0 0 4pt; }
  h2 { font-size: 15pt; margin: 22pt 0 6pt; border-bottom: 1px solid #999; padding-bottom: 2pt; }
  h3 { font-size: 12pt; margin: 16pt 0 4pt; }
  h4 { font-size: 11pt; margin: 12pt 0 4pt; }
  table { border-collapse: collapse; width: 100%; margin: 4pt 0 8pt; }
  th, td { border: 1px solid #bbb; padding: 3pt 5pt; vertical-align: top; text-align: left; }
  thead th { background: #eee; }
  table.narrow { width: auto; min-width: 40%; }
  table.small { font-size: 8.5pt; }
  th.question { width: 32%; background: #f6f6f6; font-weight: 600; }
  .muted { color: #555; }
  .found { margin-top: 3pt; color: #333; font-size: 9pt; }
  .foundTitle { font-weight: 600; color: #555; }
  .found ul { margin: 2pt 0 0 14pt; padding: 0; }
  .blank { min-height: 28pt; margin: 0 0 3pt; border-bottom: 1px dotted #999; }
  .meta td, .meta th { border: none; padding: 1pt 8pt 1pt 0; }
  .pageBreak { page-break-before: always; }
  @media print { body { margin: 0; } }
`

/** The questionnaire as a self-contained HTML document (it opens in Word too). */
export function questionnaireHtml(input: DocumentInput): string {
  const { answers } = input
  const title = `Information Systems Questionnaire${answers.organisation?.trim() ? ` – ${answers.organisation.trim()}` : ''}`
  const meta = [
    ['Organisation', answers.organisation?.trim()],
    ['Prepared by', answers.preparedBy?.trim()],
    ['Date', dateFormat.format(input.generatedAt)],
  ].filter((row): row is [string, string] => !!row[1])
  const body = [
    `<h1>${escapeHtml(title)}</h1>`,
    `<table class="meta"><tbody>${meta.map(([k, v]) => `<tr><th>${escapeHtml(k)}</th><td>${escapeHtml(v)}</td></tr>`).join('')}</tbody></table>`,
    answers.introduction?.trim() ? `<p>${paragraphs(answers.introduction.trim())}</p>` : '',
    '<p class="muted">Prepared with NKeeper from its inventory of servers and from read-only queries of the database servers. “Found by NKeeper” notes are what the systems themselves report; blank spaces are for the organisation to answer.</p>',
    serversChapter(input),
    databasesChapter(input),
    backupChapter(input),
    answers.includeAppendix === false ? '' : appendixChapter(input),
  ].join('\n')
  return `<!DOCTYPE html>
<html xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:w="urn:schemas-microsoft-com:office:word" xmlns="http://www.w3.org/TR/REC-html40">
<head><meta charset="utf-8"><title>${escapeHtml(title)}</title><style>${STYLE}</style></head>
<body>
${body}
</body>
</html>
`
}

/** Saves the document: as a Word document (.doc, which Word opens and can save as .docx or PDF) or as a web page to print. */
export function downloadQuestionnaire(html: string, fileName: string, kind: 'word' | 'html') {
  const blob =
    kind === 'word' ? new Blob(['﻿', html], { type: 'application/msword' }) : new Blob([html], { type: 'text/html;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `${fileName}.${kind === 'word' ? 'doc' : 'html'}`
  link.click()
  URL.revokeObjectURL(url)
}
