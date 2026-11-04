import { useId } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import type { SqlOutput, ToolScript, ToolSection } from '../databaseApi'
import styles from './Database.module.css'
import { ResultGrid } from './ResultGrid'

interface ReportSectionsProps {
  sections: ToolSection[]
  /** Opens a SQL script in a new query window. */
  onOpenQuery?: (sql: string) => void
  /** Shows a script to copy and run elsewhere (RMAN, SQL*Plus). */
  onShowScript?: (script: ToolScript) => void
  /** A list of the sections to jump to, when there are this many or more (0: never). */
  navigateFrom?: number
}

/** Grids as they are; runs of messages joined into one block, as a procedure that prints its output reads best. */
function grouped(results: SqlOutput[]): SqlOutput[] {
  const out: SqlOutput[] = []
  for (const result of results) {
    const last = out.at(-1)
    if (result.kind !== 'results' && last && last.kind !== 'results') {
      out[out.length - 1] = { ...last, text: `${last.text ?? ''}\n${result.text ?? ''}` }
    } else {
      out.push(result)
    }
  }
  return out
}

/**
 * A report's sections, each collapsible: its command's grids and messages, its error, and the scripts it offers.
 * A long report starts with a list of its sections to jump to.
 */
export function ReportSections({ sections, onOpenQuery, onShowScript, navigateFrom = 3 }: ReportSectionsProps) {
  const prefix = useId()
  const anchor = (index: number) => `${prefix}-section-${index}`

  function jumpTo(index: number) {
    const section = document.getElementById(anchor(index))
    if (section instanceof HTMLDetailsElement) {
      section.open = true
    }
    section?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }

  return (
    <>
      {navigateFrom > 0 && sections.length >= navigateFrom ? (
        <nav className={styles.reportNav} aria-label="Sections">
          <span>On this page:</span>
          {sections.map((section, index) => (
            <button key={anchor(index)} type="button" onClick={() => jumpTo(index)}>
              {section.title}
              {section.error ? ' (failed)' : ''}
            </button>
          ))}
        </nav>
      ) : null}
      {sections.map((section, sectionIndex) => (
        <details key={anchor(sectionIndex)} id={anchor(sectionIndex)} className={styles.reportSection} open>
          <summary>
            <h3>{section.title}</h3>
            <code>{section.command}</code>
          </summary>
          {section.note ? <p className={styles.hint}>{section.note}</p> : null}
          {section.error ? <Alert variant="error">{section.error}</Alert> : null}
          {grouped(section.results).map((result, index) =>
            result.kind === 'results' ? (
              <div key={index} className={styles.reportGrid}>
                {result.rows && result.rows.length > 0 ? <ResultGrid output={result} label={`${section.title} results`} /> : <p className={styles.hint}>No rows.</p>}
              </div>
            ) : (
              <pre key={index} className={styles.reportMessage}>
                {result.text}
              </pre>
            ),
          )}
          {!section.error && section.results.length === 0 ? <p className={styles.hint}>Nothing returned.</p> : null}
          {section.scripts && section.scripts.length > 0 ? (
            <div className={styles.reportScripts} aria-label={`Scripts for ${section.title}`}>
              {section.scripts.map((script) => (
                <Button
                  key={script.label}
                  size="sm"
                  variant="secondary"
                  title={script.text}
                  onClick={() => (script.kind === 'sql' ? onOpenQuery?.(script.text) : onShowScript?.(script))}
                >
                  {script.label}
                  {script.kind === 'sql' ? '' : '…'}
                </Button>
              ))}
            </div>
          ) : null}
        </details>
      ))}
    </>
  )
}
