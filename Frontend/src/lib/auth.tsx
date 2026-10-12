import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { ApiError, get, onUnauthorized, post } from './api'
import type { Me } from './types'

type AuthState = {
    user: Me | null
    loading: boolean
    login: (email: string, password: string, rememberMe: boolean) => Promise<void>
    logout: () => Promise<void>
    refresh: () => Promise<void>
    can: (permission: string) => boolean
    inWorkspace: (workspace: string) => boolean
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
    const [user, setUser] = useState<Me | null>(null)
    const [loading, setLoading] = useState(true)

    const refresh = useCallback(async () => {
        try {
            setUser(await get<Me>('/auth/me'))
        } catch (error) {
            if (error instanceof ApiError && (error.status === 401 || error.status === 403)) setUser(null)
            else throw error
        }
    }, [])

    useEffect(() => {
        refresh()
            .catch(() => setUser(null))
            .finally(() => setLoading(false))
    }, [refresh])

    // Any 401 from the API (expired session, deactivated user) signs the UI out.
    useEffect(() => onUnauthorized(() => setUser(null)), [])

    const login = useCallback(async (email: string, password: string, rememberMe: boolean) => {
        setUser(await post<Me>('/auth/login', { email, password, rememberMe }))
    }, [])

    const logout = useCallback(async () => {
        try {
            await post('/auth/logout')
        } finally {
            setUser(null)
        }
    }, [])

    const value = useMemo<AuthState>(
        () => ({
            user,
            loading,
            login,
            logout,
            refresh,
            can: (permission) => !!user?.permissions.includes(permission),
            inWorkspace: (workspace) => !!user?.workspaces.includes(workspace),
        }),
        [user, loading, login, logout, refresh],
    )

    return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth() {
    const ctx = useContext(AuthContext)
    if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
    return ctx
}

export const P = {
    casesViewAll: 'cases.view.all',
    casesViewSensitive: 'cases.view.sensitive',
    casesCreate: 'cases.create',
    casesSubmit: 'cases.submit',
    casesEdit: 'cases.edit',
    workflowOverride: 'workflow.override',
    reportsView: 'reports.view',
    aiUse: 'ai.use',
    auditView: 'audit.view',
    adminUsers: 'admin.users',
    adminTeams: 'admin.teams',
    adminRoles: 'admin.roles',
    adminSettings: 'admin.settings',
} as const

export const WS = {
    operations: 'Operations',
    cse: 'CSE',
    mofsl: 'MOFSL',
    mis: 'MIS',
    admin: 'Admin',
} as const
