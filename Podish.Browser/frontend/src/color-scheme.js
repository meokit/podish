import scheme from './color-scheme.json'

function hexToRgb(hex) {
    const normalized = hex.replace('#', '')
    const value = normalized.length === 3
        ? normalized.split('').map(char => char + char).join('')
        : normalized

    const int = Number.parseInt(value, 16)
    return {
        r: (int >> 16) & 255,
        g: (int >> 8) & 255,
        b: int & 255,
    }
}

function rgba(hex, alpha) {
    const {r, g, b} = hexToRgb(hex)
    return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

export function applyColorScheme(root = document.documentElement) {
    const {background, foreground, cursor, ansi, ui} = scheme

    root.style.setProperty('--theme-bg', background)
    root.style.setProperty('--theme-fg', foreground)
    root.style.setProperty('--theme-cursor', cursor)
    root.style.setProperty('--theme-accent', ui.accent)
    root.style.setProperty('--theme-accent-alt', ui.accentAlt)
    root.style.setProperty('--theme-success', ui.success)
    root.style.setProperty('--theme-warning', ui.warning)
    root.style.setProperty('--theme-danger', ui.danger)

    root.style.setProperty('--theme-surface-1', 'color-mix(in srgb, var(--theme-bg) 92%, white)')
    root.style.setProperty('--theme-surface-2', 'color-mix(in srgb, var(--theme-bg) 84%, white)')
    root.style.setProperty('--theme-surface-3', 'color-mix(in srgb, var(--theme-bg) 74%, white)')
    root.style.setProperty('--theme-border', 'color-mix(in srgb, var(--theme-fg) 14%, var(--theme-bg))')
    root.style.setProperty('--theme-border-strong', 'color-mix(in srgb, var(--theme-fg) 22%, var(--theme-bg))')
    root.style.setProperty('--theme-text-muted', 'color-mix(in srgb, var(--theme-fg) 62%, var(--theme-bg))')
    root.style.setProperty('--theme-text-subtle', 'color-mix(in srgb, var(--theme-fg) 42%, var(--theme-bg))')
    root.style.setProperty('--theme-overlay', rgba(background, 0.72))
    root.style.setProperty('--theme-selection', rgba(ui.accent, 0.3))
    root.style.setProperty('--theme-shadow', rgba(ui.accent, 0.2))
    root.style.setProperty('--theme-shadow-strong', rgba(ui.accent, 0.34))

    Object.entries(ansi).forEach(([key, value]) => {
        root.style.setProperty(`--ansi-${key}`, value)
    })
}

export function getTerminalTheme() {
    return {
        background: scheme.background,
        foreground: scheme.foreground,
        cursor: scheme.cursor,
        selectionBackground: rgba(scheme.ui.accent, 0.3),
        ...scheme.ansi,
    }
}

export default scheme
