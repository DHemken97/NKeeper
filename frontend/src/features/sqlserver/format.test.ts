import { describe, expect, it } from 'vitest'
import { errorHeading, formatElapsed, lineRange, outputText, resultSetText, scriptToRun, tabSeparated } from './format'
import type { SqlOutput } from './sqlServerApi'

const result: SqlOutput = {
  kind: 'results',
  columns: [
    { name: 'Id', type: 'int', numeric: true },
    { name: 'Name', type: 'nvarchar', numeric: false },
  ],
  rows: [
    ['1', 'alpha'],
    ['200', null],
  ],
  rowCount: 2,
}

describe('SQL Server output', () => {
  it('writes result sets as fixed-width text, numbers right-aligned and NULLs spelled out', () => {
    expect(resultSetText(result)).toBe(['Id  Name', '--- -----', '  1 alpha', '200 NULL'].join('\n'))
  })

  it('writes errors with the heading SSMS uses, in order with results and row counts', () => {
    const error: SqlOutput = { kind: 'error', text: "Invalid object name 'x'.", number: 208, severity: 16, state: 1, line: 5 }
    expect(errorHeading(error)).toBe('Msg 208, Level 16, State 1, Line 5')
    expect(errorHeading({ ...error, procedure: 'dbo.p', line: 2 })).toBe('Msg 208, Level 16, State 1, Procedure dbo.p, Line 2')
    expect(errorHeading({ kind: 'error', text: 'Query was cancelled by user.' })).toBeNull()

    expect(outputText([result, { kind: 'rows', text: '(2 rows affected)' }, error])).toBe(
      ['Id  Name', '--- -----', '  1 alpha', '200 NULL', '', '(2 rows affected)', 'Msg 208, Level 16, State 1, Line 5', "Invalid object name 'x'."].join('\n'),
    )
  })

  it('says when rows were left out', () => {
    expect(resultSetText({ ...result, rowCount: 12_000, truncated: true })).toMatch(/… 11998 more rows not shown$/)
  })

  it('copies rows as tab-separated text', () => {
    expect(tabSeparated(result.rows!, ['Id', 'Name'])).toBe('Id\tName\n1\talpha\n200\tNULL')
    expect(tabSeparated([['a\tb', 'c\nd']])).toBe('a b\tc d')
  })

  it('runs the selection when there is one, keeping its line in the editor', () => {
    const text = 'SELECT 1;\nSELECT 2;\nSELECT 3;'
    expect(scriptToRun(text, 0, 0)).toEqual({ sql: text, lineOffset: 0 })
    expect(scriptToRun(text, 10, 19)).toEqual({ sql: 'SELECT 2;', lineOffset: 1 })
    expect(scriptToRun(text, 9, 10)).toEqual({ sql: text, lineOffset: 0 })
    expect(lineRange(text, 2)).toEqual([10, 19])
    expect(lineRange(text, 99)).toEqual([20, 29])
  })

  it('formats elapsed time', () => {
    expect(formatElapsed(350)).toBe('350 ms')
    expect(formatElapsed(4210)).toBe('4.2 s')
    expect(formatElapsed(125_000)).toBe('2 min 05 s')
  })
})
