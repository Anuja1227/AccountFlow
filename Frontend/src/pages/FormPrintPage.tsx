import { useApplicationForm } from '../components/ApplicationFormPanel'
import { ErrorBanner, Loading } from '../components/ui'
import { date, dateTime } from '../lib/format'
import { caseUrl, Link } from '../lib/router'
import type { FormField } from '../lib/types'

/** Printable application form (print or "Save as PDF" from the browser). */
export function FormPrintPage({ referenceId }: { referenceId: string }) {
    const { form, error } = useApplicationForm(referenceId, true, '')
    if (error) return <ErrorBanner message={error} />
    if (!form) return <Loading label="Loading form…" />

    const c = form.case
    const minor = c.accountType === 'Minor'
    const sections = new Map<string, FormField[]>()
    for (const f of form.fields) sections.set(f.section, [...(sections.get(f.section) ?? []), f])

    const row = (label: string, value: string | undefined) => (
        <tr key={label}>
            <th>{label}</th>
            <td>{value || ''}</td>
        </tr>
    )

    return (
        <div className="print-page">
            <div className="print-toolbar no-print">
                <Link to={caseUrl(referenceId)}>← Back to case</Link>
                <button className="primary-button" onClick={() => window.print()}>
                    Print / Save as PDF
                </button>
            </div>

            <header className="print-header">
                <div>
                    <h1>Account Opening Application Form</h1>
                    <p>
                        {c.accountType} account · {c.channel} channel
                    </p>
                </div>
                <div className="print-meta">
                    <div>Reference ID: <strong>{c.referenceId}</strong></div>
                    <div>Request ID: {c.requestId}</div>
                    <div>Form status: {form.status}{form.sentAt ? ` (${date(form.sentAt)})` : ''}</div>
                </div>
            </header>
            {form.missing.length > 0 && <p className="print-warning">DRAFT — incomplete: {form.missing.join(', ')}</p>}

            <section>
                <h2>Client &amp; account</h2>
                <table className="print-table">
                    <tbody>
                        {row('Client name', c.clientName)}
                        {row('PAN', c.panNo)}
                        {row('Account type', c.accountType)}
                        {row('Segment', c.segment)}
                        {row('Location', c.location)}
                        {row('Relationship manager', c.relationshipManager)}
                        {row('CSE', c.cse)}
                    </tbody>
                </table>
            </section>

            {[...sections.entries()].map(([title, fields]) => (
                <section key={title}>
                    <h2>{title}</h2>
                    <table className="print-table">
                        <tbody>{fields.map((f) => row(f.label, form.data[f.key]))}</tbody>
                    </table>
                </section>
            ))}

            <section className="print-declaration">
                <h2>Declaration</h2>
                <p>
                    I confirm that the information given in this form is true and complete, and I agree to inform the
                    broker of any change. (Sample form for demonstration — synthetic data only.)
                </p>
                <div className="signature-row">
                    <div className="signature-box">{minor ? 'Signature of guardian' : 'Signature of applicant'}</div>
                    <div className="signature-box">Place</div>
                    <div className="signature-box">Date</div>
                </div>
            </section>

            <footer className="print-footer">
                Prepared by Operations{form.updatedByName ? ` (${form.updatedByName})` : ''}
                {form.updatedAt ? ` on ${dateTime(form.updatedAt)}` : ''} · AccountFlow
            </footer>
        </div>
    )
}
