// Thin fetch wrapper. All requests go to the same origin (/api is proxied by
// Vite in development), and the HttpOnly auth cookie is sent automatically.

export class ApiError extends Error {
    status: number
    code?: string

    constructor(status: number, message: string, code?: string) {
        super(message)
        this.status = status
        this.code = code
    }
}

type Listener = () => void
const unauthorizedListeners = new Set<Listener>()

/** Notified whenever the API answers 401 (session expired or user deactivated). */
export function onUnauthorized(listener: Listener) {
    unauthorizedListeners.add(listener)
    return () => {
        unauthorizedListeners.delete(listener)
    }
}

async function readError(response: Response): Promise<ApiError> {
    let message = `Request failed (${response.status}).`
    let code: string | undefined
    try {
        const body = await response.json()
        if (body?.message) message = body.message
        else if (body?.title) {
            const details = body.errors
                ? Object.values(body.errors as Record<string, string[]>).flat().join(' ')
                : ''
            message = details ? `${body.title} ${details}` : body.title
        }
        code = body?.code
    } catch {
        /* non-JSON error body */
    }
    if (response.status === 403 && message.startsWith('Request failed')) {
        message = 'You do not have permission to do that.'
    }
    if (response.status === 429) message = 'Too many requests. Please wait a moment and try again.'
    return new ApiError(response.status, message, code)
}

export async function api<T>(path: string, options: { method?: string; body?: unknown; signal?: AbortSignal } = {}): Promise<T> {
    let response: Response
    try {
        response = await fetch(`/api${path}`, {
            method: options.method ?? 'GET',
            credentials: 'same-origin',
            headers: options.body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
            body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
            signal: options.signal,
        })
    } catch (error) {
        if ((error as Error).name === 'AbortError') throw error
        throw new ApiError(0, 'Cannot reach the AccountFlow API. Make sure the backend is running.')
    }

    if (response.status === 401 && !path.startsWith('/auth/login')) {
        unauthorizedListeners.forEach((l) => l())
    }
    if (!response.ok) throw await readError(response)
    if (response.status === 204) return undefined as T

    const text = await response.text()
    return (text ? JSON.parse(text) : undefined) as T
}

/** multipart/form-data POST (file uploads). */
export async function postForm<T>(path: string, form: FormData): Promise<T> {
    let response: Response
    try {
        response = await fetch(`/api${path}`, { method: 'POST', credentials: 'same-origin', body: form })
    } catch {
        throw new ApiError(0, 'Cannot reach the AccountFlow API. Make sure the backend is running.')
    }
    if (response.status === 401) unauthorizedListeners.forEach((l) => l())
    if (!response.ok) throw await readError(response)
    const text = await response.text()
    return (text ? JSON.parse(text) : undefined) as T
}

export const attachmentUrl = (requestId: number, attachmentId: number) => `/api/requests/${requestId}/attachments/${attachmentId}`
export const documentUrl = (referenceId: string, documentId: number) => `/api/cases/${encodeURIComponent(referenceId)}/documents/${documentId}`

export const get = <T>(path: string, signal?: AbortSignal) => api<T>(path, { signal })
export const post = <T>(path: string, body: unknown = {}) => api<T>(path, { method: 'POST', body })
export const put = <T>(path: string, body: unknown) => api<T>(path, { method: 'PUT', body })
export const del = <T>(path: string) => api<T>(path, { method: 'DELETE' })

export function errorMessage(error: unknown): string {
    if (error instanceof ApiError) return error.message
    if (error instanceof Error) return error.message
    return 'Something went wrong.'
}
