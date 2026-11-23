export type Priority = 'low' | 'medium' | 'high'
export type TaskStatus = 'open' | 'completed'
export type Reminder = { at: string | null; sentAt: string | null }
export type Recurrence = { frequency: 'daily' | 'weekly' | 'monthly'; interval: number; daysOfWeek: string[] | null; endAt: string | null }

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
  reminder: Reminder | null
  recurrence: Recurrence | null
}

export type TaskQueryResponse = { items: Task[]; total: number }
export type TaskList = { id: string; name: string; color: string | null; createdAt: string; updatedAt: string }
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

export function getTasks(from: Date, to: Date, listId?: string) {
  const query = new URLSearchParams({ from: from.toISOString(), to: to.toISOString(), limit: '100', sort: 'dueAt' })
  if (listId) query.set('listId', listId)
  return request<TaskQueryResponse>(`/api/tasks?${query}`)
}

export function getLists() { return request<TaskList[]>('/api/lists') }

export function createList(input: { name: string; color: string }) {
  return request<TaskList>('/api/lists', { method: 'POST', body: JSON.stringify(input) })
}

export function createTask(input: { title: string; dueAt: string | null; priority: Priority; description?: string; listId?: string | null; reminder?: { at: string } | null; recurrence?: { frequency: 'daily' | 'weekly' | 'monthly'; interval: number; daysOfWeek?: string[]; endAt?: string } | null }) {
  return request<Task>('/api/tasks', { method: 'POST', body: JSON.stringify(input) })
}

export function updateTask(id: string, input: { title: string; dueAt: string | null; priority: Priority; description: string; listId: string | null; reminder: { at: string } | null; recurrence: { frequency: 'daily' | 'weekly' | 'monthly'; interval: number; daysOfWeek?: string[]; endAt?: string } | null }) {
  return request<Task>(`/api/tasks/${id}`, { method: 'PATCH', body: JSON.stringify(input) })
}

export function setTaskStatus(id: string, status: TaskStatus) {
  return request<Task>(`/api/tasks/${id}/${status === 'completed' ? 'complete' : 'reopen'}`, { method: 'POST' })
}

export function deleteTask(id: string) {
  return request<void>(`/api/tasks/${id}`, { method: 'DELETE' })
}
