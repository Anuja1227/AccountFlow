import { useEffect, useState, type ReactNode } from 'react'
import { P, useAuth, WS } from '../lib/auth'
import { Link, useLocation } from '../lib/router'

type NavItem = { to: string; label: string; icon: string; show: boolean }
type NavSection = { label: string; items: NavItem[]; group: boolean }

export function useNavSections(): NavSection[] {
    const { user, can, inWorkspace } = useAuth()
    if (!user) return []

    const canSeeCases = user.permissions.some((p) => p.startsWith('cases.view'))
    const desks: NavItem[] = [
        { to: '/ops', label: 'Operations desk', icon: '▣', show: inWorkspace(WS.operations) },
        { to: '/cse', label: 'CSE desk', icon: '✉', show: inWorkspace(WS.cse) },
        { to: '/mofsl', label: 'MOFSL desk', icon: '◆', show: inWorkspace(WS.mofsl) },
    ].filter((d) => d.show)
    // One desk: call it "My desk"; several (e.g. a supervisor in two teams): keep their names.
    if (desks.length === 1) desks[0] = { ...desks[0], label: 'My desk' }

    const sections: NavSection[] = [
        { label: 'Desk', group: false, items: desks },
        {
            label: 'Requests',
            group: true,
            items: [
                { to: '/ops/requests', label: 'Incoming requests', icon: '⇣', show: inWorkspace(WS.operations) && can(P.casesCreate) },
                { to: '/cse/requests', label: 'Send request', icon: '⇡', show: inWorkspace(WS.cse) && can(P.casesSubmit) },
            ],
        },
        {
            label: 'Cases',
            group: true,
            items: [
                { to: '/register', label: 'Account register', icon: '▤', show: canSeeCases },
                { to: '/ops/new', label: 'Log inward request', icon: '＋', show: inWorkspace(WS.operations) && can(P.casesCreate) },
            ],
        },
        {
            label: 'Reports',
            group: true,
            items: [{ to: '/mis', label: 'MIS dashboard', icon: '▦', show: can(P.reportsView) }],
        },
        {
            label: 'Administration',
            group: true,
            items: [
                { to: '/admin/overview', label: 'Overview', icon: '◉', show: can(P.adminUsers) && can(P.reportsView) },
                { to: '/admin/users', label: 'Users', icon: '☺', show: can(P.adminUsers) },
                { to: '/admin/roles', label: 'Roles & permissions', icon: '⚿', show: can(P.adminRoles) },
                { to: '/admin/teams', label: 'Teams', icon: '◎', show: can(P.adminTeams) },
                { to: '/admin/settings', label: 'Settings', icon: '⚙', show: can(P.adminSettings) },
                { to: '/admin/audit', label: 'Audit log', icon: '◷', show: can(P.auditView) },
                { to: '/admin/ai-usage', label: 'AI usage', icon: '✦', show: can(P.auditView) },
            ],
        },
    ]

    return sections
        .map((s) => ({ ...s, items: s.items.filter((i) => i.show) }))
        .filter((s) => s.items.length > 0)
}

const NAV_STATE_KEY = 'accountflow.nav.open'

function readOpenGroups(): Record<string, boolean> {
    try {
        return JSON.parse(localStorage.getItem(NAV_STATE_KEY) ?? '{}') as Record<string, boolean>
    } catch {
        return {}
    }
}

export function homePathFor(user: { workspaces: string[]; permissions: string[] } | null): string {
    if (!user) return '/login'
    if (user.workspaces.includes(WS.operations)) return '/ops'
    if (user.workspaces.includes(WS.cse)) return '/cse'
    if (user.workspaces.includes(WS.mofsl)) return '/mofsl'
    // Admin lands on the management overview; report-only users on the MIS dashboard.
    if (user.permissions.includes(P.adminUsers) && user.permissions.includes(P.reportsView)) return '/admin/overview'
    if (user.permissions.includes(P.reportsView)) return '/mis'
    if (user.permissions.includes(P.adminUsers)) return '/admin/users'
    if (user.permissions.includes(P.auditView)) return '/admin/audit'
    if (user.permissions.some((p) => p.startsWith('cases.view'))) return '/register'
    return '/no-access'
}

export function Layout({ children }: { children: ReactNode }) {
    const { user, logout } = useAuth()
    const sections = useNavSections()
    const { pathname } = useLocation()

    const isActive = (to: string) =>
        pathname === to || (to !== '/ops' && to !== '/cse' && pathname.startsWith(to + '/')) || (to === '/register' && pathname.startsWith('/cases/'))

    const workspaceLabel = user?.teams.map((t) => t.name).join(' · ') || user?.roles.join(' · ') || ''

    // Expandable groups: remembered per browser; a group holding the current page opens itself.
    const [openGroups, setOpenGroups] = useState<Record<string, boolean>>(readOpenGroups)
    const toggle = (label: string, open: boolean) =>
        setOpenGroups((g) => {
            const next = { ...g, [label]: open }
            try {
                localStorage.setItem(NAV_STATE_KEY, JSON.stringify(next))
            } catch {
                /* storage unavailable: keep in memory only */
            }
            return next
        })
    useEffect(() => {
        for (const s of sections) if (s.group && s.items.some((i) => isActive(i.to)) && openGroups[s.label] === false) toggle(s.label, true)
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [pathname])

    return (
        <div className="app-shell">
            <aside className="sidebar">
                <div className="brand">
                    <div className="brand-mark">A</div>
                    <div>
                        <strong>AccountFlow</strong>
                        <span>{workspaceLabel || 'Account opening'}</span>
                    </div>
                </div>

                <nav aria-label="Main navigation" className="nav-scroll">
                    {sections.map((section) => {
                        if (!section.group)
                            return (
                                <div key={section.label} className="nav-section">
                                    {section.items.map((item) => (
                                        <NavLink key={item.to} item={item} active={isActive(item.to)} />
                                    ))}
                                </div>
                            )
                        const hasActive = section.items.some((i) => isActive(i.to))
                        const open = openGroups[section.label] ?? (hasActive || section.label === 'Cases' || section.label === 'Requests')
                        const id = `nav-group-${section.label.toLowerCase()}`
                        return (
                            <div key={section.label} className="nav-section">
                                <button
                                    type="button"
                                    className={`nav-group ${hasActive ? 'has-active' : ''}`}
                                    aria-expanded={open}
                                    aria-controls={id}
                                    onClick={() => toggle(section.label, !open)}
                                >
                                    <span>{section.label}</span>
                                    <span className="nav-chevron" aria-hidden="true">
                                        {open ? '▾' : '▸'}
                                    </span>
                                </button>
                                {open && (
                                    <div id={id} className="nav-group-items">
                                        {section.items.map((item) => (
                                            <NavLink key={item.to} item={item} active={isActive(item.to)} />
                                        ))}
                                    </div>
                                )}
                            </div>
                        )
                    })}
                </nav>

                <div className="sidebar-bottom">
                    <span className="avatar" aria-hidden="true">
                        {(user?.fullName || user?.email || '?').slice(0, 1).toUpperCase()}
                    </span>
                    <div className="sidebar-user">
                        <strong>{user?.fullName}</strong>
                        <span>{user?.roles.join(', ') || 'No role assigned'}</span>
                        <div className="sidebar-links">
                            <Link to="/account">My account</Link>
                            <button type="button" className="link-button" onClick={() => void logout()}>
                                Sign out
                            </button>
                        </div>
                    </div>
                </div>
            </aside>

            <main className="main-content">{children}</main>
        </div>
    )
}

function NavLink({ item, active }: { item: NavItem; active: boolean }) {
    return (
        <Link to={item.to} className={`nav-item ${active ? 'active' : ''}`} aria-current={active ? 'page' : undefined}>
            <span className="nav-icon" aria-hidden="true">
                {item.icon}
            </span>
            {item.label}
        </Link>
    )
}
