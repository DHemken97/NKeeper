import { createContext, useContext } from 'react'
import { dbApi, DB_ENGINES, type DbApi, type DbEngine, type DbEngineInfo } from './databaseApi'

/** The engine of the database tab a component is in (SQL Server unless a tab says otherwise). */
export const DbEngineContext = createContext<DbEngine>('mssql')

/** The API a database tab hands its explorer, tools and query windows (one that reconnects the tab's session); else the engine's own. */
export const DbApiContext = createContext<DbApi | null>(null)

export function useDbEngine(): DbEngine {
  return useContext(DbEngineContext)
}

/** The API for this tab's engine: the same object for as long as the tab is open. */
export function useDbApi(): DbApi {
  const tabApi = useContext(DbApiContext)
  const engine = useDbEngine()
  return tabApi ?? dbApi(engine)
}

export function useDbEngineInfo(): DbEngineInfo {
  return DB_ENGINES[useDbEngine()]
}
