import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterEach } from 'vitest'
import { resetAntiforgeryToken } from '../lib/api'

// The first render of a test file loads lazy routes and session views, which can take longer than
// the default 1s when every test file runs in parallel.
configure({ asyncUtilTimeout: 5000 })

afterEach(() => {
  cleanup()
  resetAntiforgeryToken()
})
