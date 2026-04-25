import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App'
import {applyColorScheme} from './color-scheme'
import './index.css'

applyColorScheme()

ReactDOM.createRoot(document.getElementById('root')).render(<App/>)
