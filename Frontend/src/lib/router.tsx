import { useEffect, useState, type AnchorHTMLAttributes, type MouseEvent, type ReactNode } from 'react'

// Minimal History-API router: enough for this app without another dependency.

const listeners = new Set<() => void>()

export function navigate(to: string, options: { replace?: boolean } = {}) {
    if (options.replace) window.history.replaceState(null, '', to)
    else window.history.pushState(null, '', to)
    listeners.forEach((l) => l())
    window.scrollTo(0, 0)
}

window.addEventListener('popstate', () => listeners.forEach((l) => l()))

export function useLocation() {
    const [path, setPath] = useState(window.location.pathname + window.location.search)
    useEffect(() => {
        const update = () => setPath(window.location.pathname + window.location.search)
        listeners.add(update)
        return () => {
            listeners.delete(update)
        }
    }, [])
    const [pathname, search = ''] = path.split('?')
    return { pathname, search: new URLSearchParams(search) }
}

/** Matches "/cases/:ref" style patterns. Returns params or null. */
export function matchPath(pattern: string, pathname: string): Record<string, string> | null {
    const p = pattern.split('/').filter(Boolean)
    const s = pathname.split('/').filter(Boolean)
    if (p.length !== s.length) return null
    const params: Record<string, string> = {}
    for (let i = 0; i < p.length; i++) {
        if (p[i].startsWith(':')) params[p[i].slice(1)] = decodeURIComponent(s[i])
        else if (p[i] !== s[i]) return null
    }
    return params
}

export function Link({
    to,
    children,
    ...rest
}: { to: string; children: ReactNode } & Omit<AnchorHTMLAttributes<HTMLAnchorElement>, 'href'>) {
    function onClick(event: MouseEvent<HTMLAnchorElement>) {
        if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
        event.preventDefault()
        navigate(to)
    }
    return (
        <a href={to} onClick={onClick} {...rest}>
            {children}
        </a>
    )
}

export const caseUrl = (referenceId: string) => `/cases/${encodeURIComponent(referenceId)}`
