import { useEffect, type ReactNode } from 'react'
import { toneForStatus } from '../lib/format'

export function Loading({ label = 'Loading…' }: { label?: string }) {
    return (
        <div className="empty-state" role="status" aria-live="polite">
            <span className="spinner" aria-hidden="true" /> {label}
        </div>
    )
}

export function ErrorBanner({ message, onRetry }: { message: string; onRetry?: () => void }) {
    return (
        <div className="error-banner" role="alert">
            <span>{message}</span>
            {onRetry && (
                <button type="button" className="link-button" onClick={onRetry}>
                    Retry
                </button>
            )}
        </div>
    )
}

export function SuccessBanner({ message }: { message: string }) {
    return (
        <div className="success-banner" role="status">
            {message}
        </div>
    )
}

export function Empty({ children }: { children: ReactNode }) {
    return <div className="empty-state">{children}</div>
}

export function StatusPill({ status }: { status: string }) {
    return <span className={`status-pill ${toneForStatus(status)}`}>{status}</span>
}

/** Status as plain text with a coloured dot, so it never looks like a button. */
export function StatusDot({ status }: { status: string }) {
    return <span className={`status-dot ${toneForStatus(status)}`}>{status}</span>
}

export function Pill({ tone = 'neutral', children }: { tone?: string; children: ReactNode }) {
    return <span className={`status-pill ${tone}`}>{children}</span>
}

export function PageHeader({
    crumb,
    title,
    subtitle,
    actions,
}: {
    crumb: ReactNode
    title: string
    subtitle?: string
    actions?: ReactNode
}) {
    return (
        <header className="topbar">
            <div>
                <div className="breadcrumb">{crumb}</div>
                <h1>{title}</h1>
                {subtitle && <p className="subtitle">{subtitle}</p>}
            </div>
            {actions && <div className="topbar-actions">{actions}</div>}
        </header>
    )
}

export function Panel({
    title,
    subtitle,
    tag,
    children,
    className = '',
}: {
    title?: string
    subtitle?: string
    tag?: ReactNode
    children: ReactNode
    className?: string
}) {
    return (
        <section className={`panel ${className}`}>
            {(title || tag) && (
                <div className="panel-heading">
                    <div>
                        {title && <h2>{title}</h2>}
                        {subtitle && <p>{subtitle}</p>}
                    </div>
                    {tag && <span className="panel-tag">{tag}</span>}
                </div>
            )}
            {children}
        </section>
    )
}

export function Modal({
    title,
    onClose,
    children,
    wide = false,
}: {
    title: string
    onClose: () => void
    children: ReactNode
    wide?: boolean
}) {
    useEffect(() => {
        const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
        window.addEventListener('keydown', onKey)
        return () => window.removeEventListener('keydown', onKey)
    }, [onClose])

    return (
        <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
            <div className={`modal ${wide ? 'wide' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
                <div className="modal-header">
                    <h2>{title}</h2>
                    <button type="button" className="icon-button" onClick={onClose} aria-label="Close">
                        ×
                    </button>
                </div>
                <div className="modal-body">{children}</div>
            </div>
        </div>
    )
}

export function Field({ label, children, help }: { label: string; children: ReactNode; help?: string | null }) {
    return (
        <label className="field">
            <span>{label}</span>
            {children}
            {help && <small>{help}</small>}
        </label>
    )
}

export function KeyValue({ items }: { items: [string, ReactNode][] }) {
    return (
        <dl className="kv-grid">
            {items.map(([k, v]) => (
                <div key={k}>
                    <dt>{k}</dt>
                    <dd>{v ?? '—'}</dd>
                </div>
            ))}
        </dl>
    )
}
