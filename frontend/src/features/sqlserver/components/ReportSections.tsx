import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import type { SqlOutput, ToolScript, ToolSection } from '../sqlServerApi'
import { ResultGrid } from './ResultGrid'
import styles from './SqlServer.module.css'

interface ReportSectionsProps {
  sections: ToolSection[]
  /** Opens a SQL script in a new query window. */
  onOpenQuery?: (sql: string) => void
  /** Shows a script to copy and run elsewhere (RMAN, SQL*Plus). */
  onShowScript?: (script: ToolScript) => void
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

/** A report's sections: each command's grids and messages, its error, and the scripts it offers. */
export function ReportSections({ sections, onOpenQuery, onShowScript }: ReportSectionsProps) {
  return (
    <>
      {sections.map((section, sectionIndex) => (
        <section key={`${section.title}-${sectionIndex}`} className={styles.replicationSection}>
          <h3>
            {section.title} <code>{section.command}</code>
          </h3>
          {section.note ? <p className={styles.hint}>{section.note}</p> : null}
          {section.error ? <Alert variant="error">{section.error}</Alert> : null}
          {grouped(section.results).map((result, index) =>
            result.kind === 'results' ? (
              <div key={index} className={styles.replicationGrid}>
                {result.rows && result.rows.length > 0 ? <ResultGrid output={result} label={`${section.title} results`} /> : <p className={styles.hint}>No rows.</p>}
              </div>
            ) : (
              <pre key={index} className={styles.replicationMessage}>
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
        </section>
      ))}
    </>
  )
}
