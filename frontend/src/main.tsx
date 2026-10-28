import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './app/App'
import { exposeRuntime } from './sdk/runtime'
import './styles/global.css'

// Plugins' scripts use the web app's React and SDK.
exposeRuntime()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
