import type { SqlOutput } from './databaseApi'

/** Widest a column gets in text results; longer values are cut, as SSMS cuts them. */
const MAX_TEXT_WIDTH = 256

/** An error's heading, as SSMS writes it: "Msg 208, Level 16, State 1, Procedure p, Line 5". */
export function errorHeading(output: SqlOutput): string | null {
  if (output.kind !== 'error' || output.number == null) {
    return null
  }
  return [
    `Msg ${output.number}`,
    `Level ${output.severity ?? 0}`,
    `State ${output.state ?? 0}`,
    ...(output.procedure ? [`Procedure ${output.procedure}`] : []),
    ...(output.line != null ? [`Line ${output.line}`] : []),
  ].join(', ')
}

/** A value as the grid and text show it. */
export function displayValue(value: string | null): string {
  return value ?? 'NULL'
}

/** One result set as fixed-width text: header, dashes, rows (numbers right-aligned, headers left-aligned). */
export function resultSetText(output: SqlOutput): string {
  const columns = output.columns ?? []
  const rows = output.rows ?? []
  const clip = (text: string) => (text.length > MAX_TEXT_WIDTH ? text.slice(0, MAX_TEXT_WIDTH) : text).replace(/[\r\n\t]+/g, ' ')
  const widths = columns.map((column, i) => Math.min(MAX_TEXT_WIDTH, Math.max(column.name.length, ...rows.map((row) => clip(displayValue(row[i])).length))))
  const cell = (text: string, i: number) => (columns[i].numeric ? clip(text).padStart(widths[i]) : clip(text).padEnd(widths[i]))
  const lines = [
    columns.map((column, i) => clip(column.name).padEnd(widths[i])).join(' '),
    widths.map((width) => '-'.repeat(width)).join(' '),
    ...rows.map((row) => row.map((value, i) => cell(displayValue(value), i)).join(' ')),
  ]
  if (output.truncated) {
    lines.push(`… ${(output.rowCount ?? rows.length) - rows.length} more rows not shown`)
  }
  return lines.map((line) => line.trimEnd()).join('\n')
}

/** Everything a query produced, as "Results to Text" shows it. */
export function outputText(output: SqlOutput[]): string {
  return output
    .map((item) => {
      if (item.kind === 'results') {
        return `${resultSetText(item)}\n`
      }
      const heading = errorHeading(item)
      return heading ? `${heading}\n${item.text ?? ''}` : (item.text ?? '')
    })
    .join('\n')
}

/** Tab-separated rows, for pasting into a spreadsheet. */
export function tabSeparated(rows: (string | null)[][], header?: string[]): string {
  const clean = (value: string | null) => displayValue(value).replace(/[\t\r\n]+/g, ' ')
  return [...(header ? [header.map((h) => clean(h))] : []), ...rows.map((row) => row.map(clean))].map((row) => row.join('\t')).join('\n')
}

/** "350 ms", "4.2 s", "2 min 05 s". */
export function formatElapsed(ms: number): string {
  if (ms < 1000) {
    return `${ms} ms`
  }
  if (ms < 60_000) {
    return `${(ms / 1000).toFixed(1)} s`
  }
  const seconds = Math.round(ms / 1000)
  return `${Math.floor(seconds / 60)} min ${String(seconds % 60).padStart(2, '0')} s`
}

/**
 * The part of a script to run: the selection when there is one (with the lines above it, so errors
 * point at the editor's lines), else everything.
 */
export function scriptToRun(text: string, selectionStart: number, selectionEnd: number): { sql: string; lineOffset: number } {
  if (selectionEnd > selectionStart && text.slice(selectionStart, selectionEnd).trim()) {
    return { sql: text.slice(selectionStart, selectionEnd), lineOffset: text.slice(0, selectionStart).split('\n').length - 1 }
  }
  return { sql: text, lineOffset: 0 }
}

/** The offsets of a 1-based line in a text, to select it. */
export function lineRange(text: string, line: number): [number, number] {
  const lines = text.split('\n')
  const index = Math.min(Math.max(line, 1), lines.length) - 1
  const start = lines.slice(0, index).reduce((sum, l) => sum + l.length + 1, 0)
  return [start, start + lines[index].length]
}
