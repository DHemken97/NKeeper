import { createContext, useContext } from 'react'
import { dbApi, DB_ENGINES, type DbApi, type DbEngine, type DbEngineInfo } from './sqlServerApi'

/** The engine of the database tab a component is in (SQL Server unless a tab says otherwise). */
export const DbEngineContext = createContext<DbEngine>('mssql')

export function useDbEngine(): DbEngine {
  return useContext(DbEngineContext)
}

/** The API for this tab's engine: the same object for as long as the tab is open. */
export function useDbApi(): DbApi {
  return dbApi(useDbEngine())
}

export function useDbEngineInfo(): DbEngineInfo {
  return DB_ENGINES[useDbEngine()]
}
