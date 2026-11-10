import { api } from '../../../lib/api'
import { closeOnPageExit } from '../../../lib/pageExit'
import type { CertificatePrompt } from '../../terminal/kvm'
import { dbApi, type DatabaseSurvey } from '../../databases/databaseApi'
import type { DatabaseInstance, QuestionnaireAnswers } from './model'

/** The key the questionnaire's answers are saved under. */
export const ANSWERS_KEY = 'insurance'

export interface SavedAnswers {
  answers: QuestionnaireAnswers
  updatedAtUtc: string | null
  updatedBy: string | null
}

export const answersApi = {
  get: (signal?: AbortSignal) => api.get<SavedAnswers>(`/api/reports/answers/${ANSWERS_KEY}`, { signal }),
  save: (answers: QuestionnaireAnswers) => api.put<SavedAnswers>(`/api/reports/answers/${ANSWERS_KEY}`, { body: { answers } }),
}

export type SurveyOutcome =
  | { status: 'done'; survey: DatabaseSurvey }
  /** The server's certificate must be trusted first (SQL Server); `renewed` when it replaces a trusted one. */
  | { status: 'trust'; certificate: CertificatePrompt; renewed: boolean }

/**
 * Surveys a database server: opens a session with the credential, reads the survey, and closes the session.
 * Pass the fingerprint the user chose to trust after a "trust" outcome.
 */
export async function surveyInstance(instance: DatabaseInstance, credentialId: string, trustFingerprint?: string): Promise<SurveyOutcome> {
  const database = dbApi(instance.engine)
  const opened = await database.open({ hostId: instance.host.id, serviceIndex: instance.serviceIndex, credentialId }, trustFingerprint)
  if (opened.status === 'trustRequired' || !opened.sessionId) {
    return { status: 'trust', certificate: opened.certificate!, renewed: !!opened.renewed }
  }
  const sessionId = opened.sessionId
  const forget = closeOnPageExit(database.sessionPath(sessionId))
  try {
    return { status: 'done', survey: await database.survey(sessionId) }
  } finally {
    forget()
    database.close(sessionId).catch(() => {
      // It closes itself when idle.
    })
  }
}
