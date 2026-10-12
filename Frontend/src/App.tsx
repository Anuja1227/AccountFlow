import { useEffect, type ReactNode } from 'react'
import './App.css'
import { homePathFor, Layout } from './components/Layout'
import { Empty, Loading, PageHeader } from './components/ui'
import { AuthProvider, P, useAuth, WS } from './lib/auth'
import { matchPath, navigate, useLocation } from './lib/router'
import { CaseDetailPage } from './pages/CaseDetailPage'
import { DeskPage } from './pages/DeskPage'
import { FormPrintPage } from './pages/FormPrintPage'
import { LoginPage } from './pages/LoginPage'
import { MisDashboard } from './pages/MisDashboard'
import { NewCasePage } from './pages/NewCasePage'
import { RegisterPage } from './pages/RegisterPage'
import { IncomingRequestsPage, SendRequestPage } from './pages/RequestsPages'
import { AccountPage, AuditPage, RolesPage, SettingsPage, TeamsPage, UsersPage } from './pages/admin/AdminPages'

function Forbidden() {
    return (
        <>
            <PageHeader crumb="Access" title="Not available" />
            <Empty>You do not have access to this page. If you need it, ask your administrator to adjust your role or team.</Empty>
        </>
    )
}

function Routes() {
    const { user, loading, can, inWorkspace } = useAuth()
    const { pathname } = useLocation()

    // The login page is the entry point: nothing else renders without a session.
    useEffect(() => {
        if (loading) return
        if (!user && pathname !== '/login') navigate('/login', { replace: true })
        if (user && (pathname === '/login' || pathname === '/')) navigate(homePathFor(user), { replace: true })
    }, [user, loading, pathname])

    if (loading) return <Loading label="Starting AccountFlow…" />
    if (!user) return pathname === '/login' ? <LoginPage /> : <Loading />

    const canSeeCases = user.permissions.some((p) => p.startsWith('cases.view'))
    const guard = (allowed: boolean, element: ReactNode) => (allowed ? element : <Forbidden />)

    // Printable application form: no app chrome.
    const printMatch = matchPath('/cases/:ref/form/print', pathname)
    if (printMatch) return guard(canSeeCases, <FormPrintPage referenceId={printMatch.ref} />)

    let page: ReactNode
    const caseMatch = matchPath('/cases/:ref', pathname)

    if (caseMatch) page = guard(canSeeCases, <CaseDetailPage key={caseMatch.ref} referenceId={caseMatch.ref} />)
    else
        switch (pathname) {
            case '/ops':
                page = guard(inWorkspace(WS.operations), <DeskPage workspace="Operations" />)
                break
            case '/ops/new':
                page = guard(inWorkspace(WS.operations) && can(P.casesCreate), <NewCasePage />)
                break
            case '/cse':
                page = guard(inWorkspace(WS.cse), <DeskPage workspace="CSE" />)
                break
            case '/cse/requests':
                page = guard(inWorkspace(WS.cse) && can(P.casesSubmit), <SendRequestPage />)
                break
            case '/ops/requests':
                page = guard(inWorkspace(WS.operations) && can(P.casesCreate), <IncomingRequestsPage />)
                break
            case '/mofsl':
                page = guard(inWorkspace(WS.mofsl), <DeskPage workspace="MOFSL" />)
                break
            case '/register':
                page = guard(canSeeCases, <RegisterPage />)
                break
            case '/mis':
                page = guard(can(P.reportsView), <MisDashboard />)
                break
            case '/admin/users':
                page = guard(can(P.adminUsers), <UsersPage />)
                break
            case '/admin/roles':
                page = guard(can(P.adminRoles), <RolesPage />)
                break
            case '/admin/teams':
                page = guard(can(P.adminTeams), <TeamsPage />)
                break
            case '/admin/settings':
                page = guard(can(P.adminSettings), <SettingsPage />)
                break
            case '/admin/audit':
                page = guard(can(P.auditView), <AuditPage key="audit" view="audit" />)
                break
            case '/admin/ai-usage':
                page = guard(can(P.auditView), <AuditPage key="ai" view="ai" />)
                break
            case '/account':
                page = <AccountPage />
                break
            case '/no-access':
                page = (
                    <>
                        <PageHeader crumb="Access" title="No workspace assigned" />
                        <Empty>Your account has no roles or teams yet. Ask your administrator to assign them.</Empty>
                    </>
                )
                break
            default:
                page = (
                    <>
                        <PageHeader crumb="Not found" title="Page not found" />
                        <Empty>That page does not exist.</Empty>
                    </>
                )
        }

    return <Layout>{page}</Layout>
}

export default function App() {
    return (
        <AuthProvider>
            <Routes />
        </AuthProvider>
    )
}
