export type Priority = 'low' | 'medium' | 'high'
export type TaskStatus = 'open' | 'completed'

export type Task = {
  id: string
  title: string
  description: string
  dueAt: string | null
  priority: Priority
  status: TaskStatus
  tags: string[]
  listId: string | null
  createdAt: string
  updatedAt: string
}

export type TaskQueryResponse = { items: Task[]; total: number }
export type Credentials = { username: string; password: string }

const tokenKey = 'kalendar.accessToken'

export function getToken() {
  return localStorage.getItem(tokenKey)
}

export function setToken(token: string | null) {
  if (token) localStorage.setItem(tokenKey, token)
  else localStorage.removeItem(tokenKey)
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  const token = getToken()
  if (token) headers.set('Authorization', `Bearer ${token}`)
  const response = await fetch(path, { ...init, headers })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { title?: string; errors?: Record<string, string[]> } | null
    const message = body?.title ?? Object.values(body?.errors ?? {})[0]?.[0] ?? `Request failed (${response.status})`
    throw new Error(message)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export async function authenticate(path: '/api/auth/login' | '/api/auth/register', credentials: Credentials) {
  const result = await request<{ accessToken?: string }>(path, { method: 'POST', body: JSON.stringify(credentials) })
  if (result.accessToken) setToken(result.accessToken)
  return result
}

export function getTasks(from: Date, to: Date) {
  const query = new URLSearchParams({ from: from.toISOString(), to: to.toISOString(), limit: '100', sort: 'dueAt' })
  return request<TaskQueryResponse>(`/api/tasks?${query}`)
}

export function createTask(input: { title: string; dueAt: string | null; priority: Priority; description?: string }) {
  return request<Task>('/api/tasks', { method: 'POST', body: JSON.stringify(input) })
}

export function updateTask(id: string, input: { title: string; dueAt: string | null; priority: Priority; description: string }) {
  return request<Task>(`/api/tasks/${id}`, { method: 'PATCH', body: JSON.stringify(input) })
}

export function setTaskStatus(id: string, status: TaskStatus) {
  return request<Task>(`/api/tasks/${id}/${status === 'completed' ? 'complete' : 'reopen'}`, { method: 'POST' })
}

export function deleteTask(id: string) {
  return request<void>(`/api/tasks/${id}`, { method: 'DELETE' })
}
