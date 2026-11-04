import { ApiError } from '../../lib/api'
import type { DbApi } from './sqlServerApi'

type SessionCall<A extends unknown[], R> = (sessionId: string, ...args: A) => Promise<R>

/**
 * The API as a database tab's explorer and Management tools use it: calls on the tab's own session go to
 * whichever server session is live now, and when the server has closed it (idle, or restarted), they quietly
 * open a new one and carry on. The tab keeps one id (`mainId`) for its whole life; `current` is the live one,
 * or null while disconnected. Other sessions (query windows have their own) pass straight through.
 */
export function reconnectingApi(db: DbApi, mainId: string, current: () => string | null, reopen: () => Promise<string>): DbApi {
  function wrap<A extends unknown[], R>(call: SessionCall<A, R>): SessionCall<A, R> {
    return async (sessionId, ...args) => {
      if (sessionId !== mainId) {
        return call(sessionId, ...args)
      }
      const id = current() ?? (await reopen())
      try {
        return await call(id, ...args)
      } catch (error) {
        // Closed since the last keep-alive: nothing ran, so connect again and do it there.
        if (!(error instanceof ApiError && error.code === 'session_not_found')) {
          throw error
        }
        return call(await reopen(), ...args)
      }
    }
  }

  return {
    ...db,
    databases: wrap(db.databases),
    changeDatabase: wrap(db.changeDatabase),
    explore: wrap(db.explore),
    script: wrap(db.script),
    blocking: wrap(db.blocking),
    orphans: wrap(db.orphans),
    fixOrphan: wrap(db.fixOrphan),
    fixManyOrphans: wrap(db.fixManyOrphans),
    fixMatchedOrphans: wrap(db.fixMatchedOrphans),
    kill: wrap(db.kill),
    replication: wrap(db.replication),
    report: wrap(db.report),
    resumeConnection: wrap(db.resumeConnection),
  }
}
