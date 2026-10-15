import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'
import { resetAntiforgeryToken } from '../lib/api'

afterEach(() => {
  cleanup()
  resetAntiforgeryToken()
})
