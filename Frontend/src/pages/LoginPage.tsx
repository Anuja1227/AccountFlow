import { useState, type FormEvent } from 'react'
import { errorMessage } from '../lib/api'
import { useAuth } from '../lib/auth'

export function LoginPage() {
    const { login } = useAuth()
    const [email, setEmail] = useState('')
    const [password, setPassword] = useState('')
    const [rememberMe, setRememberMe] = useState(false)
    const [error, setError] = useState('')
    const [busy, setBusy] = useState(false)

    async function submit(event: FormEvent) {
        event.preventDefault()
        setError('')
        setBusy(true)
        try {
            await login(email.trim(), password, rememberMe)
        } catch (err) {
            setError(errorMessage(err))
            setPassword('')
        } finally {
            setBusy(false)
        }
    }

    return (
        <div className="login-shell">
            <section className="login-hero" aria-hidden="true">
                <div className="brand light">
                    <div className="brand-mark">A</div>
                    <div>
                        <strong>AccountFlow</strong>
                        <span>Account opening workflow &amp; MIS</span>
                    </div>
                </div>
                <div className="login-hero-copy">
                    <h2>One register for every account-opening request.</h2>
                    <p>
                        Operations, CSE and MOFSL teams work the same case through six stages, with queries, resubmissions,
                        RFT, TAT and SLA tracked automatically.
                    </p>
                    <ol className="login-stages">
                        {['CSE request', 'Form preparation', 'Signed form', 'Resolution', 'MOFSL', 'Account opened'].map((s, i) => (
                            <li key={s}>
                                <span>{i + 1}</span>
                                {s}
                            </li>
                        ))}
                    </ol>
                </div>
            </section>

            <section className="login-panel">
                <form className="login-card" onSubmit={submit} noValidate>
                    <h1>Sign in</h1>
                    <p className="subtitle">Use the account your administrator created for you.</p>

                    {error && (
                        <div className="error-banner" role="alert">
                            {error}
                        </div>
                    )}

                    <label className="field">
                        <span>Email</span>
                        <input
                            type="email"
                            autoComplete="username"
                            value={email}
                            onChange={(e) => setEmail(e.target.value)}
                            required
                            autoFocus
                        />
                    </label>

                    <label className="field">
                        <span>Password</span>
                        <input
                            type="password"
                            autoComplete="current-password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            required
                        />
                    </label>

                    <label className="checkbox">
                        <input type="checkbox" checked={rememberMe} onChange={(e) => setRememberMe(e.target.checked)} />
                        Keep me signed in on this device
                    </label>

                    <button type="submit" className="primary-button full" disabled={busy || !email || !password}>
                        {busy ? 'Signing in…' : 'Sign in'}
                    </button>

                    <p className="muted small">
                        There is no self-registration. Contact your AccountFlow administrator for access or a password reset.
                    </p>
                </form>
            </section>
        </div>
    )
}
