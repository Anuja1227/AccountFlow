import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { Empty, ErrorBanner, Field, Loading, Modal, PageHeader, Pill, SuccessBanner } from '../../components/ui'
import { del, errorMessage, get, post, put } from '../../lib/api'
import { useAuth } from '../../lib/auth'
import { date, dateTime } from '../../lib/format'
import type { AuditEvent, Lookups } from '../../lib/types'

type PermissionDef = { code: string; group: string; description: string }
type RoleRow = {
    id: number
    name: string
    description: string
    isSystemRole: boolean
    isActive: boolean
    locked: boolean
    permissions: string[]
    userCount: number
}
type TeamRow = { id: number; name: string; workspaceType: string; description: string | null; isActive: boolean; memberCount: number }
type UserRow = {
    id: number
    email: string
    fullName: string
    cseName: string | null
    isActive: boolean
    lastLoginAt: string | null
    lockedOut: boolean
    roles: { roleId: number; name: string }[]
    teams: { teamId: number; name: string; workspaceType: string }[]
    overrideCount: number
}
type UserDetail = {
    user: {
        id: number
        email: string
        fullName: string
        cseName: string | null
        isActive: boolean
        roleIds: number[]
        teamIds: number[]
        overrides: { permission: string; effect: string }[]
    }
    effectivePermissions: string[]
    workspaces: string[]
}

const WORKSPACES = ['Operations', 'CSE', 'MOFSL', 'MIS', 'Admin']

function useAdminData() {
    const [roles, setRoles] = useState<RoleRow[]>([])
    const [teams, setTeams] = useState<TeamRow[]>([])
    const [permissions, setPermissions] = useState<PermissionDef[]>([])
    useEffect(() => {
        get<RoleRow[]>('/admin/roles').then(setRoles).catch(() => undefined)
        get<TeamRow[]>('/admin/teams').then(setTeams).catch(() => undefined)
        get<PermissionDef[]>('/admin/permissions').then(setPermissions).catch(() => undefined)
    }, [])
    return { roles, teams, permissions }
}

// ------------------------------------------------------------------ Users

export function UsersPage() {
    const { user: me } = useAuth()
    const [users, setUsers] = useState<UserRow[]>([])
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')
    const [notice, setNotice] = useState('')
    const [editing, setEditing] = useState<number | 'new' | null>(null)
    const [search, setSearch] = useState('')

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            setUsers(await get<UserRow[]>('/admin/users'))
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [])

    useEffect(() => {
        void load()
    }, [load])

    async function toggle(u: UserRow) {
        setError('')
        try {
            await post(`/admin/users/${u.id}/status`, { isActive: !u.isActive })
            setNotice(`${u.fullName} ${u.isActive ? 'deactivated — they are signed out on their next request' : 'activated'}.`)
            await load()
        } catch (err) {
            setError(errorMessage(err))
        }
    }

    const filtered = users.filter((u) => `${u.fullName} ${u.email} ${u.cseName ?? ''}`.toLowerCase().includes(search.toLowerCase()))

    return (
        <>
            <PageHeader
                crumb="Administration / Users"
                title="Users"
                subtitle="Create accounts, assign roles and teams, set per-user permission overrides, and activate or deactivate access."
                actions={
                    <button className="primary-button" onClick={() => setEditing('new')}>
                        ＋ New user
                    </button>
                }
            />
            {notice && <SuccessBanner message={notice} />}
            {error && <ErrorBanner message={error} onRetry={() => void load()} />}
            <section className="panel register-panel">
                <div className="register-tools">
                    <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search users…" aria-label="Search users" />
                    <span className="muted small">{filtered.length} users</span>
                </div>
                {loading ? (
                    <Loading />
                ) : (
                    <div className="table-scroll">
                        <table>
                            <thead>
                                <tr>
                                    <th>User</th>
                                    <th>Roles</th>
                                    <th>Teams</th>
                                    <th>CSE name</th>
                                    <th>Overrides</th>
                                    <th>Last sign-in</th>
                                    <th>Status</th>
                                    <th />
                                </tr>
                            </thead>
                            <tbody>
                                {filtered.map((u) => (
                                    <tr key={u.id}>
                                        <td>
                                            <strong>{u.fullName}</strong>
                                            <span className="cell-subtitle">{u.email}</span>
                                        </td>
                                        <td>{u.roles.map((r) => r.name).join(', ') || <span className="muted">None</span>}</td>
                                        <td>{u.teams.map((t) => `${t.name} (${t.workspaceType})`).join(', ') || <span className="muted">None</span>}</td>
                                        <td>{u.cseName ?? '—'}</td>
                                        <td>{u.overrideCount || '—'}</td>
                                        <td>{u.lastLoginAt ? dateTime(u.lastLoginAt) : 'Never'}</td>
                                        <td>
                                            <Pill tone={u.isActive ? 'success' : 'neutral'}>{u.isActive ? 'Active' : 'Inactive'}</Pill>
                                            {u.lockedOut && <Pill tone="danger">Locked</Pill>}
                                        </td>
                                        <td className="row-actions">
                                            <button className="clear-button" onClick={() => setEditing(u.id)}>
                                                Edit
                                            </button>
                                            {u.id !== me?.id && (
                                                <button className="clear-button" onClick={() => void toggle(u)}>
                                                    {u.isActive ? 'Deactivate' : 'Activate'}
                                                </button>
                                            )}
                                        </td>
                                    </tr>
                                ))}
                                {filtered.length === 0 && (
                                    <tr>
                                        <td colSpan={8} className="empty-state">
                                            No users found.
                                        </td>
                                    </tr>
                                )}
                            </tbody>
                        </table>
                    </div>
                )}
            </section>
            {editing !== null && (
                <UserEditor
                    userId={editing === 'new' ? null : editing}
                    onClose={() => setEditing(null)}
                    onSaved={(msg) => {
                        setEditing(null)
                        setNotice(msg)
                        void load()
                    }}
                />
            )}
        </>
    )
}

function UserEditor({ userId, onClose, onSaved }: { userId: number | null; onClose: () => void; onSaved: (msg: string) => void }) {
    const { roles, teams, permissions } = useAdminData()
    const [cses, setCses] = useState<string[]>([])
    const [detail, setDetail] = useState<UserDetail | null>(null)
    const [form, setForm] = useState({ email: '', fullName: '', cseName: '', isActive: true, password: '' })
    const [roleIds, setRoleIds] = useState<number[]>([])
    const [teamIds, setTeamIds] = useState<number[]>([])
    const [overrides, setOverrides] = useState<{ permission: string; effect: string }[]>([])
    const [resetPassword, setResetPassword] = useState('')
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)

    useEffect(() => {
        get<Lookups>('/lookups').then((l) => setCses(l.cses)).catch(() => undefined)
        if (userId == null) return
        get<UserDetail>(`/admin/users/${userId}`)
            .then((d) => {
                setDetail(d)
                setForm({ email: d.user.email, fullName: d.user.fullName, cseName: d.user.cseName ?? '', isActive: d.user.isActive, password: '' })
                setRoleIds(d.user.roleIds)
                setTeamIds(d.user.teamIds)
                setOverrides(d.user.overrides)
            })
            .catch((err) => setError(errorMessage(err)))
    }, [userId])

    // Live preview of effective permissions from the current selection.
    const preview = useMemo(() => {
        const set = new Set<string>()
        roles.filter((r) => roleIds.includes(r.id) && r.isActive).forEach((r) => r.permissions.forEach((p) => set.add(p)))
        overrides.filter((o) => o.effect === 'Grant').forEach((o) => set.add(o.permission))
        overrides.filter((o) => o.effect === 'Deny').forEach((o) => set.delete(o.permission))
        return [...set].sort()
    }, [roles, roleIds, overrides])

    const toggleId = (list: number[], id: number) => (list.includes(id) ? list.filter((x) => x !== id) : [...list, id])

    async function submit(e: FormEvent) {
        e.preventDefault()
        setBusy(true)
        setError('')
        const body = {
            email: form.email.trim(),
            fullName: form.fullName.trim(),
            cseName: form.cseName.trim() || null,
            isActive: form.isActive,
            roleIds,
            teamIds,
            overrides: overrides.filter((o) => o.permission),
        }
        try {
            if (userId == null) {
                await post('/admin/users', { ...body, password: form.password })
                onSaved(`User ${body.fullName} created.`)
            } else {
                await put(`/admin/users/${userId}`, body)
                if (resetPassword) await post(`/admin/users/${userId}/reset-password`, { newPassword: resetPassword })
                onSaved(`User ${body.fullName} updated.${resetPassword ? ' Password reset.' : ''}`)
            }
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    const needsCseName = teams.some((t) => teamIds.includes(t.id) && t.workspaceType === 'CSE')

    return (
        <Modal title={userId == null ? 'New user' : `Edit ${detail?.user.fullName ?? 'user'}`} onClose={onClose} wide>
            {userId != null && !detail && !error ? (
                <Loading />
            ) : (
                <form onSubmit={submit} className="form-grid">
                    <Field label="Full name *">
                        <input value={form.fullName} onChange={(e) => setForm({ ...form, fullName: e.target.value })} required maxLength={150} />
                    </Field>
                    <Field label="Email (sign-in) *">
                        <input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} required maxLength={256} />
                    </Field>
                    {userId == null ? (
                        <Field label="Initial password *" help="Min 10 characters with upper, lower, digit and symbol. Share it securely; the user can change it under My account.">
                            <input type="password" autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} required minLength={10} />
                        </Field>
                    ) : (
                        <Field label="Reset password (optional)" help="Leave blank to keep the current password.">
                            <input type="password" autoComplete="new-password" value={resetPassword} onChange={(e) => setResetPassword(e.target.value)} minLength={10} />
                        </Field>
                    )}
                    <Field label={`CSE name${needsCseName ? ' (required for CSE case access)' : ''}`} help="Must match the CSE name on case records. Limits ‘assigned cases’ access to this CSE.">
                        <input value={form.cseName} onChange={(e) => setForm({ ...form, cseName: e.target.value })} list="ue-cses" maxLength={150} />
                    </Field>
                    <datalist id="ue-cses">{cses.map((c) => <option key={c} value={c} />)}</datalist>

                    <fieldset className="checkbox-group">
                        <legend>Roles</legend>
                        {roles.map((r) => (
                            <label key={r.id} className="checkbox">
                                <input type="checkbox" checked={roleIds.includes(r.id)} onChange={() => setRoleIds((l) => toggleId(l, r.id))} />
                                {r.name} {!r.isActive && <span className="muted">(inactive)</span>}
                            </label>
                        ))}
                    </fieldset>

                    <fieldset className="checkbox-group">
                        <legend>Teams</legend>
                        {teams.map((t) => (
                            <label key={t.id} className="checkbox">
                                <input type="checkbox" checked={teamIds.includes(t.id)} onChange={() => setTeamIds((l) => toggleId(l, t.id))} />
                                {t.name} <span className="muted">({t.workspaceType})</span>
                            </label>
                        ))}
                    </fieldset>

                    <fieldset className="span-all overrides">
                        <legend>Permission overrides</legend>
                        <p className="muted small">Grant adds a permission the roles do not give; Deny removes one (Deny always wins).</p>
                        {overrides.map((o, i) => (
                            <div key={i} className="override-row">
                                <select value={o.permission} onChange={(e) => setOverrides((l) => l.map((x, j) => (j === i ? { ...x, permission: e.target.value } : x)))}>
                                    <option value="">Select permission…</option>
                                    {permissions.map((p) => (
                                        <option key={p.code} value={p.code}>
                                            {p.code} — {p.description}
                                        </option>
                                    ))}
                                </select>
                                <select value={o.effect} onChange={(e) => setOverrides((l) => l.map((x, j) => (j === i ? { ...x, effect: e.target.value } : x)))}>
                                    <option>Grant</option>
                                    <option>Deny</option>
                                </select>
                                <button type="button" className="clear-button" onClick={() => setOverrides((l) => l.filter((_, j) => j !== i))}>
                                    Remove
                                </button>
                            </div>
                        ))}
                        <button type="button" className="clear-button" onClick={() => setOverrides((l) => [...l, { permission: '', effect: 'Grant' }])}>
                            ＋ Add override
                        </button>
                    </fieldset>

                    <label className="checkbox span-all">
                        <input type="checkbox" checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} /> Active (can sign in)
                    </label>

                    <div className="span-all effective">
                        <strong>Effective permissions ({preview.length})</strong>
                        <div className="chips">
                            {preview.length === 0 ? <span className="muted small">None — this user could sign in but see nothing.</span> : preview.map((p) => <span key={p} className="chip">{p}</span>)}
                        </div>
                    </div>

                    {error && <div className="error-banner span-all">{error}</div>}
                    <div className="form-actions span-all">
                        <button type="button" className="secondary-button" onClick={onClose}>
                            Cancel
                        </button>
                        <button type="submit" className="primary-button" disabled={busy}>
                            {busy ? 'Saving…' : 'Save user'}
                        </button>
                    </div>
                </form>
            )}
        </Modal>
    )
}

// ------------------------------------------------------------------ Roles

export function RolesPage() {
    const [roles, setRoles] = useState<RoleRow[]>([])
    const [permissions, setPermissions] = useState<PermissionDef[]>([])
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')
    const [notice, setNotice] = useState('')
    const [editing, setEditing] = useState<RoleRow | 'new' | null>(null)

    const load = useCallback(async () => {
        setLoading(true)
        try {
            const [r, p] = await Promise.all([get<RoleRow[]>('/admin/roles'), get<PermissionDef[]>('/admin/permissions')])
            setRoles(r)
            setPermissions(p)
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [])

    useEffect(() => {
        void load()
    }, [load])

    async function remove(role: RoleRow) {
        if (!window.confirm(`Delete role "${role.name}"?`)) return
        try {
            await del(`/admin/roles/${role.id}`)
            setNotice(`Role ${role.name} deleted.`)
            await load()
        } catch (err) {
            setError(errorMessage(err))
        }
    }

    return (
        <>
            <PageHeader
                crumb="Administration / Roles"
                title="Roles & permissions"
                subtitle="A role is a named set of permissions. Assign roles to users; teams decide which workflow steps they own."
                actions={
                    <button className="primary-button" onClick={() => setEditing('new')}>
                        ＋ New role
                    </button>
                }
            />
            {notice && <SuccessBanner message={notice} />}
            {error && <ErrorBanner message={error} />}
            {loading ? (
                <Loading />
            ) : (
                <div className="card-grid">
                    {roles.map((r) => (
                        <section key={r.id} className="panel role-card">
                            <div className="panel-heading">
                                <div>
                                    <h2>{r.name}</h2>
                                    <p>{r.description || 'No description'}</p>
                                </div>
                                <div className="chips">
                                    {r.locked && <Pill tone="info">Locked</Pill>}
                                    {!r.isActive && <Pill>Inactive</Pill>}
                                    <Pill>{r.userCount} user(s)</Pill>
                                </div>
                            </div>
                            <div className="chips">
                                {r.permissions.map((p) => (
                                    <span key={p} className="chip">
                                        {p}
                                    </span>
                                ))}
                            </div>
                            {!r.locked && (
                                <div className="row-actions">
                                    <button className="clear-button" onClick={() => setEditing(r)}>
                                        Edit
                                    </button>
                                    <button className="clear-button" onClick={() => void remove(r)} disabled={r.userCount > 0} title={r.userCount > 0 ? 'Remove from users first' : undefined}>
                                        Delete
                                    </button>
                                </div>
                            )}
                        </section>
                    ))}
                </div>
            )}
            {editing && (
                <RoleEditor
                    role={editing === 'new' ? null : editing}
                    permissions={permissions}
                    onClose={() => setEditing(null)}
                    onSaved={(msg) => {
                        setEditing(null)
                        setNotice(msg)
                        void load()
                    }}
                />
            )}
        </>
    )
}

function RoleEditor({ role, permissions, onClose, onSaved }: { role: RoleRow | null; permissions: PermissionDef[]; onClose: () => void; onSaved: (msg: string) => void }) {
    const [name, setName] = useState(role?.name ?? '')
    const [description, setDescription] = useState(role?.description ?? '')
    const [isActive, setIsActive] = useState(role?.isActive ?? true)
    const [selected, setSelected] = useState<string[]>(role?.permissions ?? [])
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)

    const groups = useMemo(() => {
        const m = new Map<string, PermissionDef[]>()
        permissions.forEach((p) => m.set(p.group, [...(m.get(p.group) ?? []), p]))
        return [...m.entries()]
    }, [permissions])

    async function submit(e: FormEvent) {
        e.preventDefault()
        setBusy(true)
        setError('')
        try {
            const body = { name: name.trim(), description: description.trim(), isActive, permissions: selected }
            if (role) await put(`/admin/roles/${role.id}`, body)
            else await post('/admin/roles', body)
            onSaved(`Role ${body.name} saved.`)
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <Modal title={role ? `Edit role: ${role.name}` : 'New role'} onClose={onClose} wide>
            <form onSubmit={submit} className="form-grid">
                <Field label="Role name *">
                    <input value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
                </Field>
                <Field label="Description">
                    <input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} />
                </Field>
                {groups.map(([group, perms]) => (
                    <fieldset key={group} className="checkbox-group span-all">
                        <legend>{group}</legend>
                        {perms.map((p) => (
                            <label key={p.code} className="checkbox">
                                <input type="checkbox" checked={selected.includes(p.code)} onChange={() => setSelected((s) => (s.includes(p.code) ? s.filter((x) => x !== p.code) : [...s, p.code]))} />
                                <span>
                                    <code>{p.code}</code> — {p.description}
                                </span>
                            </label>
                        ))}
                    </fieldset>
                ))}
                <label className="checkbox span-all">
                    <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} /> Active
                </label>
                {error && <div className="error-banner span-all">{error}</div>}
                <div className="form-actions span-all">
                    <button type="button" className="secondary-button" onClick={onClose}>
                        Cancel
                    </button>
                    <button type="submit" className="primary-button" disabled={busy}>
                        {busy ? 'Saving…' : 'Save role'}
                    </button>
                </div>
            </form>
        </Modal>
    )
}

// ------------------------------------------------------------------ Teams

export function TeamsPage() {
    const [teams, setTeams] = useState<TeamRow[]>([])
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')
    const [notice, setNotice] = useState('')
    const [editing, setEditing] = useState<TeamRow | 'new' | null>(null)

    const load = useCallback(async () => {
        setLoading(true)
        try {
            setTeams(await get<TeamRow[]>('/admin/teams'))
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [])

    useEffect(() => {
        void load()
    }, [load])

    return (
        <>
            <PageHeader
                crumb="Administration / Teams"
                title="Teams"
                subtitle="Each team belongs to a workspace (Operations, CSE, MOFSL, MIS or Admin). Members of a team carry that workspace’s workflow responsibilities."
                actions={
                    <button className="primary-button" onClick={() => setEditing('new')}>
                        ＋ New team
                    </button>
                }
            />
            {notice && <SuccessBanner message={notice} />}
            {error && <ErrorBanner message={error} />}
            <section className="panel register-panel">
                {loading ? (
                    <Loading />
                ) : (
                    <div className="table-scroll">
                        <table>
                            <thead>
                                <tr>
                                    <th>Team</th>
                                    <th>Workspace</th>
                                    <th>Members</th>
                                    <th>Status</th>
                                    <th />
                                </tr>
                            </thead>
                            <tbody>
                                {teams.map((t) => (
                                    <tr key={t.id}>
                                        <td>
                                            <strong>{t.name}</strong>
                                            <span className="cell-subtitle">{t.description}</span>
                                        </td>
                                        <td>{t.workspaceType}</td>
                                        <td>{t.memberCount}</td>
                                        <td>
                                            <Pill tone={t.isActive ? 'success' : 'neutral'}>{t.isActive ? 'Active' : 'Inactive'}</Pill>
                                        </td>
                                        <td>
                                            <button className="clear-button" onClick={() => setEditing(t)}>
                                                Edit
                                            </button>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                )}
            </section>
            {editing && (
                <TeamEditor
                    team={editing === 'new' ? null : editing}
                    onClose={() => setEditing(null)}
                    onSaved={(msg) => {
                        setEditing(null)
                        setNotice(msg)
                        void load()
                    }}
                />
            )}
        </>
    )
}

function TeamEditor({ team, onClose, onSaved }: { team: TeamRow | null; onClose: () => void; onSaved: (msg: string) => void }) {
    const [form, setForm] = useState({
        name: team?.name ?? '',
        workspaceType: team?.workspaceType ?? 'Operations',
        description: team?.description ?? '',
        isActive: team?.isActive ?? true,
    })
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)

    async function submit(e: FormEvent) {
        e.preventDefault()
        setBusy(true)
        setError('')
        try {
            if (team) await put(`/admin/teams/${team.id}`, form)
            else await post('/admin/teams', form)
            onSaved(`Team ${form.name} saved.`)
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <Modal title={team ? `Edit team: ${team.name}` : 'New team'} onClose={onClose}>
            <form onSubmit={submit} className="action-form">
                <Field label="Team name *">
                    <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required maxLength={100} />
                </Field>
                <Field label="Workspace *">
                    <select value={form.workspaceType} onChange={(e) => setForm({ ...form, workspaceType: e.target.value })}>
                        {WORKSPACES.map((w) => (
                            <option key={w}>{w}</option>
                        ))}
                    </select>
                </Field>
                <Field label="Description">
                    <textarea rows={2} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} maxLength={500} />
                </Field>
                <label className="checkbox">
                    <input type="checkbox" checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} /> Active
                </label>
                {error && <div className="error-banner">{error}</div>}
                <div className="form-actions">
                    <button type="button" className="secondary-button" onClick={onClose}>
                        Cancel
                    </button>
                    <button type="submit" className="primary-button" disabled={busy}>
                        {busy ? 'Saving…' : 'Save team'}
                    </button>
                </div>
            </form>
        </Modal>
    )
}

// ------------------------------------------------------------------ Settings

export function SettingsPage() {
    const [sla, setSla] = useState('')
    const [mode, setMode] = useState('Automatic')
    const [limits, setLimits] = useState({ minSla: 1, maxSla: 60 })
    const [error, setError] = useState('')
    const [notice, setNotice] = useState('')
    const [busy, setBusy] = useState(false)

    useEffect(() => {
        get<{ slaThresholdWorkingDays: number; minSla: number; maxSla: number; aiAutomationMode: string }>('/admin/settings')
            .then((s) => {
                setSla(String(s.slaThresholdWorkingDays))
                setMode(s.aiAutomationMode)
                setLimits({ minSla: s.minSla, maxSla: s.maxSla })
            })
            .catch((err) => setError(errorMessage(err)))
    }, [])

    async function submit(e: FormEvent) {
        e.preventDefault()
        setBusy(true)
        setError('')
        try {
            await put('/admin/settings', { slaThresholdWorkingDays: Number(sla), aiAutomationMode: mode })
            setNotice('Settings saved. TAT/SLA views and AI automation use them immediately.')
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <>
            <PageHeader crumb="Administration / Settings" title="Settings" subtitle="System configuration used by deterministic TAT and SLA calculations." />
            {notice && <SuccessBanner message={notice} />}
            {error && <ErrorBanner message={error} />}
            <form className="panel action-form narrow" onSubmit={submit}>
                <Field label="SLA threshold (working days)" help={`A case breaches SLA when Gross TAT exceeds this. Allowed ${limits.minSla}–${limits.maxSla}. Default 7.`}>
                    <input type="number" min={limits.minSla} max={limits.maxSla} value={sla} onChange={(e) => setSla(e.target.value)} required />
                </Field>
                <Field
                    label="AI automation mode"
                    help="Automatic: AI raises queries, follows up on incomplete CSE replies and moves complete cases on by itself (labelled “AccountFlow AI” in History). Suggest only: AI records a recommendation that Operations applies with one click. Off: no automatic AI processing."
                >
                    <select value={mode} onChange={(e) => setMode(e.target.value)}>
                        <option value="Automatic">Automatic</option>
                        <option value="SuggestOnly">Suggest only (one-click approve)</option>
                        <option value="Off">Off</option>
                    </select>
                </Field>
                <div className="form-actions">
                    <button type="submit" className="primary-button" disabled={busy}>
                        {busy ? 'Saving…' : 'Save settings'}
                    </button>
                </div>
            </form>
        </>
    )
}

// ------------------------------------------------------------------ Audit

type AiUsage = {
    id: number
    feature: string
    referenceId: string | null
    provider: string
    model: string
    success: boolean
    error: string | null
    durationMs: number
    accepted: boolean | null
    createdAt: string
    userName: string
}

export function AuditPage({ view = 'audit' }: { view?: 'audit' | 'ai' }) {
    const tab = view
    const [events, setEvents] = useState<AuditEvent[]>([])
    const [ai, setAi] = useState<AiUsage[]>([])
    const [reference, setReference] = useState('')
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState('')

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            if (tab === 'audit') setEvents(await get<AuditEvent[]>(`/admin/audit?take=300${reference ? `&referenceId=${encodeURIComponent(reference)}` : ''}`))
            else setAi(await get<AiUsage[]>('/admin/ai-usage?take=300'))
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setLoading(false)
        }
    }, [tab, reference])

    useEffect(() => {
        void load()
    }, [load])

    return (
        <>
            {tab === 'audit' ? (
                <PageHeader crumb="Administration / Audit log" title="Audit log" subtitle="Sign-ins, workflow actions and admin changes. Client data is never written to the log." />
            ) : (
                <PageHeader crumb="Administration / AI usage" title="AI usage" subtitle="Every AI call: feature, provider, model, duration and outcome. Prompts and client data are not stored." />
            )}
            {error && <ErrorBanner message={error} />}
            <section className="panel register-panel">
                <div className="register-tools">
                    {tab === 'audit' && <input value={reference} onChange={(e) => setReference(e.target.value)} placeholder="Filter by Reference ID" aria-label="Filter by reference" />}
                </div>
                {loading ? (
                    <Loading />
                ) : tab === 'audit' ? (
                    events.length === 0 ? (
                        <Empty>No events.</Empty>
                    ) : (
                        <div className="table-scroll">
                            <table>
                                <thead>
                                    <tr>
                                        <th>When</th>
                                        <th>User</th>
                                        <th>Action</th>
                                        <th>Case</th>
                                        <th>Status change</th>
                                        <th>Details</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {events.map((e) => (
                                        <tr key={e.id}>
                                            <td>{dateTime(e.createdAt)}</td>
                                            <td>
                                                {e.userName}
                                                {e.team && <span className="cell-subtitle">{e.team}</span>}
                                            </td>
                                            <td>{e.action}</td>
                                            <td>{e.referenceId ?? '—'}</td>
                                            <td>{e.toStatus ? `${e.fromStatus ?? ''} → ${e.toStatus}` : '—'}</td>
                                            <td className="wrap">{e.details ?? ''}</td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    )
                ) : ai.length === 0 ? (
                    <Empty>No AI calls yet.</Empty>
                ) : (
                    <div className="table-scroll">
                        <table>
                            <thead>
                                <tr>
                                    <th>When</th>
                                    <th>User</th>
                                    <th>Feature</th>
                                    <th>Case</th>
                                    <th>Provider / model</th>
                                    <th>Result</th>
                                    <th>Time</th>
                                    <th>Used?</th>
                                </tr>
                            </thead>
                            <tbody>
                                {ai.map((l) => (
                                    <tr key={l.id}>
                                        <td>{dateTime(l.createdAt)}</td>
                                        <td>{l.userName}</td>
                                        <td>{l.feature}</td>
                                        <td>{l.referenceId ?? '—'}</td>
                                        <td>
                                            {l.provider} / {l.model}
                                        </td>
                                        <td>{l.success ? <Pill tone="success">OK</Pill> : <Pill tone="danger">{l.error ?? 'Failed'}</Pill>}</td>
                                        <td>{(l.durationMs / 1000).toFixed(1)} s</td>
                                        <td>{l.accepted == null ? '—' : l.accepted ? 'Used' : 'Discarded'}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                )}
            </section>
        </>
    )
}

// ------------------------------------------------------------------ My account

export function AccountPage() {
    const { user } = useAuth()
    const [current, setCurrent] = useState('')
    const [next, setNext] = useState('')
    const [confirm, setConfirm] = useState('')
    const [error, setError] = useState('')
    const [notice, setNotice] = useState('')
    const [busy, setBusy] = useState(false)

    async function submit(e: FormEvent) {
        e.preventDefault()
        setError('')
        if (next !== confirm) {
            setError('The new passwords do not match.')
            return
        }
        setBusy(true)
        try {
            await post('/auth/change-password', { currentPassword: current, newPassword: next })
            setNotice('Password changed.')
            setCurrent('')
            setNext('')
            setConfirm('')
        } catch (err) {
            setError(errorMessage(err))
        } finally {
            setBusy(false)
        }
    }

    return (
        <>
            <PageHeader crumb="My account" title={user?.fullName ?? 'My account'} subtitle={user?.email} />
            <section className="panel narrow">
                <h2>Access</h2>
                <p className="small">
                    <strong>Roles:</strong> {user?.roles.join(', ') || 'None'}
                    <br />
                    <strong>Teams:</strong> {user?.teams.map((t) => `${t.name} (${t.workspaceType})`).join(', ') || 'None'}
                    <br />
                    <strong>CSE name:</strong> {user?.cseName ?? '—'}
                    <br />
                    <strong>Last updated:</strong> {date(new Date().toISOString())}
                </p>
                <div className="chips">
                    {user?.permissions.map((p) => (
                        <span key={p} className="chip">
                            {p}
                        </span>
                    ))}
                </div>
            </section>
            {notice && <SuccessBanner message={notice} />}
            {error && <ErrorBanner message={error} />}
            <form className="panel action-form narrow" onSubmit={submit}>
                <h2>Change password</h2>
                <Field label="Current password">
                    <input type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
                </Field>
                <Field label="New password" help="Min 10 characters with upper, lower, digit and symbol.">
                    <input type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} required minLength={10} />
                </Field>
                <Field label="Confirm new password">
                    <input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
                </Field>
                <div className="form-actions">
                    <button type="submit" className="primary-button" disabled={busy}>
                        {busy ? 'Saving…' : 'Change password'}
                    </button>
                </div>
            </form>
        </>
    )
}
