import { FormEvent, useEffect, useMemo, useState } from 'react'
import { authenticate, createList, createTask, deleteTask, getLists, getTasks, getToken, Priority, setTaskStatus, setToken, Task, TaskList, updateTask } from './api'
import { addDays, agendaDateLabel, calendarDays, dateRangeLabel, dayLabel, monthLabel, monthStart, nextMonth, previousMonth, sameDay, taskDay, timeLabel, toLocalInputValue, weekDays, weekdayLabel } from './date'

function Icon({ name }: { name: 'plus' | 'chevron-left' | 'chevron-right' | 'search' | 'logout' }) {
  const paths = {
    plus: <><path d="M12 5v14M5 12h14" /></>,
    'chevron-left': <path d="m15 18-6-6 6-6" />,
    'chevron-right': <path d="m9 18 6-6-6-6" />,
    search: <><circle cx="11" cy="11" r="6.5" /><path d="m16 16 4 4" /></>,
    logout: <><path d="M10 5H5v14h5" /><path d="m15 8 4 4-4 4M19 12H9" /></>,
  }
  return <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">{paths[name]}</svg>
}

const recurrenceDayOptions = [['monday', 'M'], ['tuesday', 'T'], ['wednesday', 'W'], ['thursday', 'T'], ['friday', 'F'], ['saturday', 'S'], ['sunday', 'S']] as const

function AuthScreen({ onAuthenticated }: { onAuthenticated: () => void }) {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault(); setError(''); setBusy(true)
    try {
      if (mode === 'register') await authenticate('/api/auth/register', { username, password })
      await authenticate('/api/auth/login', { username, password })
      localStorage.setItem('kalendar.username', username); onAuthenticated()
    }
    catch (caught) { setError(caught instanceof Error ? caught.message : 'Не удалось выполнить вход.') }
    finally { setBusy(false) }
  }

  return <main className="auth-page">
    <section className="auth-panel" aria-labelledby="auth-title">
      <div className="brand-mark" aria-hidden="true">K</div>
      <p className="eyebrow">A calmer way to plan</p>
      <h1 id="auth-title">Your time,<br /><em>in view.</em></h1>
      <p className="auth-copy">A simple calendar for the things that matter today and the days ahead.</p>
      <form onSubmit={submit} className="auth-form">
        <label>Username<input name="username" value={username} onChange={event => setUsername(event.target.value)} autoComplete="username" spellCheck={false} required /></label>
        <label>Password<input name="password" value={password} onChange={event => setPassword(event.target.value)} type="password" autoComplete={mode === 'login' ? 'current-password' : 'new-password'} minLength={8} required /></label>
        {error && <p className="form-error" role="alert">{error}</p>}
        <button className="button button-primary button-wide" disabled={busy}>{busy ? 'Working…' : mode === 'login' ? 'Sign in' : 'Create account'}</button>
      </form>
      <button className="text-button" onClick={() => { setMode(mode === 'login' ? 'register' : 'login'); setError('') }}>
        {mode === 'login' ? 'New here? Create an account' : 'Already have an account? Sign in'}
      </button>
    </section>
    <aside className="auth-aside"><span>Plan with intention.</span><span>Leave room for living.</span></aside>
  </main>
}

function ListModal({ onClose, onCreated }: { onClose: () => void; onCreated: (list: TaskList) => void }) {
  const [name, setName] = useState('')
  const [color, setColor] = useState('#B76845')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  async function submit(event: FormEvent) {
    event.preventDefault(); setError(''); setBusy(true)
    try { const list = await createList({ name, color }); onCreated(list); onClose() }
    catch (caught) { setError(caught instanceof Error ? caught.message : 'Could not create list.') }
    finally { setBusy(false) }
  }
  return <div className="modal-backdrop" role="presentation" onMouseDown={event => event.target === event.currentTarget && onClose()}>
    <section className="modal compact-modal" role="dialog" aria-modal="true" aria-labelledby="list-modal-title">
      <div className="modal-header"><div><p className="eyebrow">New list</p><h2 id="list-modal-title">Give it a place.</h2></div><button className="icon-button" aria-label="Close" onClick={onClose}>×</button></div>
      <form onSubmit={submit} className="task-form"><label>Name<input name="list-name" autoFocus value={name} onChange={event => setName(event.target.value)} placeholder="e.g. Personal…" maxLength={100} required /></label><label>Color<div className="color-input"><input aria-label="List color" type="color" value={color} onChange={event => setColor(event.target.value.toUpperCase())} /><span>{color}</span></div></label>{error && <p className="form-error" role="alert">{error}</p>}<div className="modal-actions"><div className="modal-danger" /><button type="button" className="button button-quiet" onClick={onClose}>Cancel</button><button className="button button-primary" disabled={busy}>{busy ? 'Saving…' : 'Create list'}</button></div></form>
    </section>
  </div>
}

function WeekView({ days, tasksFor, lists, onCreate, onOpen }: { days: Date[]; tasksFor: (day: Date) => Task[]; lists: TaskList[]; onCreate: (day: Date) => void; onOpen: (task: Task) => void }) {
  const [now, setNow] = useState(new Date())
  const startHour = 6
  const endHour = 23
  const hourHeight = 64
  const eventDurationMinutes = 45
  const eventHeight = eventDurationMinutes / 60 * hourHeight
  const totalGridHeight = (endHour - startHour) * hourHeight
  const hours = Array.from({ length: endHour - startHour }, (_, index) => startHour + index)
  const listColors = useMemo(() => new Map(lists.map(list => [list.id, list.color ?? '#88978c'])), [lists])

  useEffect(() => {
    const timer = window.setInterval(() => setNow(new Date()), 60_000)
    return () => window.clearInterval(timer)
  }, [])

  function layoutEvents(day: Date) {
    const scheduled = tasksFor(day).flatMap(task => {
      const date = taskDay(task.dueAt)
      const minutes = date ? date.getHours() * 60 + date.getMinutes() : 0
      return date ? [{ task, minutes, overflow: minutes < startHour * 60 ? 'before' : minutes >= endHour * 60 ? 'after' : null }] : []
    }).sort((a, b) => a.minutes - b.minutes)
    const clusters: typeof scheduled[] = []
    let cluster: typeof scheduled = []
    let clusterEnd = -Infinity
    scheduled.forEach(entry => {
      if (cluster.length && entry.minutes >= clusterEnd) { clusters.push(cluster); cluster = [] }
      cluster.push(entry)
      clusterEnd = Math.max(clusterEnd, entry.minutes + eventDurationMinutes)
    })
    if (cluster.length) clusters.push(cluster)

    return clusters.flatMap(group => {
      const laneEnds: number[] = []
      const assigned = group.map(entry => {
        let lane = 0
        while (laneEnds[lane] !== undefined && laneEnds[lane] > entry.minutes) lane += 1
        laneEnds[lane] = entry.minutes + eventDurationMinutes
        return { ...entry, lane }
      })
      const laneCount = laneEnds.length
      return assigned.map(entry => ({ ...entry, laneCount }))
    })
  }

  const nowMinutes = now.getHours() * 60 + now.getMinutes()
  const nowTop = (nowMinutes - startHour * 60) / 60 * hourHeight
  const nowVisible = nowMinutes >= startHour * 60 && nowMinutes <= endHour * 60

  return <section className="week-view" aria-label="Week calendar">
    <div className="week-scroll"><div className="week-days-header"><div className="week-time-gutter" />{days.map(day => <div className={`week-date-header ${sameDay(day, now) ? 'is-today' : ''}`} key={day.toISOString()}><span>{weekdayLabel(day)}</span><strong>{dayLabel(day)}</strong>{sameDay(day, now) && <em>Today</em>}<button onClick={() => onCreate(day)} aria-label={`Add task on ${weekdayLabel(day)} ${dayLabel(day)}`}><Icon name="plus" /></button></div>)}</div><div className="week-body"><div className="week-time-axis">{hours.map(hour => <span key={hour}>{`${String(hour).padStart(2, '0')}:00`}</span>)}</div><div className="week-day-columns">{days.map(day => { const events = layoutEvents(day); return <div className={`week-day-column ${sameDay(day, now) ? 'is-today' : ''}`} key={day.toISOString()}><div className="week-hour-lines">{hours.map(hour => <span key={hour} />)}</div>{sameDay(day, now) && nowVisible && <div className="now-line" style={{ top: nowTop }}><i /> </div>}<div className="week-event-layer">{events.map(({ task, minutes, lane, laneCount, overflow }) => { const top = Math.max(0, Math.min(totalGridHeight - eventHeight, (minutes - startHour * 60) / 60 * hourHeight)); const color = listColors.get(task.listId ?? '') ?? '#88978c'; return <button className={`week-event priority-${task.priority} ${task.status === 'completed' ? 'is-complete' : ''} ${overflow ? 'is-overflow' : ''}`} style={{ top, height: eventHeight, left: `calc(${lane * 100 / laneCount}% + 5px)`, width: `calc(${100 / laneCount}% - 10px)`, borderLeftColor: color }} key={task.id} onClick={() => onOpen(task)} title={task.title}><span>{timeLabel(new Date(task.dueAt!))}</span><strong>{task.title}</strong>{overflow === 'before' && <small>Earlier</small>}{overflow === 'after' && <small>Later</small>}{!overflow && task.reminder?.at && <small>Reminder set</small>}</button> })}</div></div> })}</div></div></div>
  </section>
}

function AgendaView({ tasks, lists, emptyDate, onCreate, onOpen }: { tasks: Task[]; lists: TaskList[]; emptyDate: Date; onCreate: (day: Date) => void; onOpen: (task: Task) => void }) {
  const grouped = new Map<string, { date: Date; tasks: Task[] }>()
  tasks.filter(task => task.dueAt).sort((a, b) => new Date(a.dueAt!).getTime() - new Date(b.dueAt!).getTime()).forEach(task => { const date = taskDay(task.dueAt)!; const key = date.toDateString(); const group = grouped.get(key); if (group) group.tasks.push(task); else grouped.set(key, { date, tasks: [task] }) })
  if (!grouped.size) return <div className="state-message empty-state agenda-empty"><strong>No scheduled tasks here.</strong><span>Add a task with a date to see it in your agenda.</span><button className="text-button" onClick={() => onCreate(emptyDate)}>Add a task</button></div>
  return <section className="agenda-list" aria-label="Agenda">{Array.from(grouped.values()).map(group => <div className="agenda-day" key={group.date.toDateString()}><div className="agenda-date"><span className="eyebrow">{weekdayLabel(group.date)}</span><strong>{agendaDateLabel(group.date)}</strong></div><div className="agenda-tasks">{group.tasks.map(task => <button className="agenda-task" key={task.id} onClick={() => onOpen(task)}><span className="agenda-time">{timeLabel(new Date(task.dueAt!))}</span><span className={`agenda-dot priority-${task.priority}`} style={{ backgroundColor: lists.find(list => list.id === task.listId)?.color ?? undefined }} /><span className={task.status === 'completed' ? 'is-complete' : ''}>{task.title}</span><span className="agenda-list-name">{lists.find(list => list.id === task.listId)?.name ?? 'No list'}</span></button>)}</div></div>)}</section>
}

function TaskModal({ onClose, onSaved, onDeleted, selectedDate, task, lists }: { onClose: () => void; onSaved: (task: Task) => void; onDeleted: (id: string) => void; selectedDate: Date; task?: Task; lists: TaskList[] }) {
  const [title, setTitle] = useState(task?.title ?? '')
  const [dueAt, setDueAt] = useState(task?.dueAt ? toLocalInputValue(new Date(task.dueAt)) : toLocalInputValue(new Date(selectedDate.getFullYear(), selectedDate.getMonth(), selectedDate.getDate(), 9, 0)))
  const [priority, setPriority] = useState<Priority>(task?.priority ?? 'medium')
  const [description, setDescription] = useState(task?.description ?? '')
  const [listId, setListId] = useState(task?.listId ?? '')
  const [reminderEnabled, setReminderEnabled] = useState(Boolean(task?.reminder?.at))
  const [reminderAt, setReminderAt] = useState(task?.reminder?.at ? toLocalInputValue(new Date(task.reminder.at)) : '')
  const [recurrenceEnabled, setRecurrenceEnabled] = useState(Boolean(task?.recurrence))
  const [recurrenceFrequency, setRecurrenceFrequency] = useState<'daily' | 'weekly' | 'monthly'>(task?.recurrence?.frequency ?? 'weekly')
  const [recurrenceInterval, setRecurrenceInterval] = useState(task?.recurrence?.interval ?? 1)
  const [recurrenceDays, setRecurrenceDays] = useState<string[]>(task?.recurrence?.daysOfWeek ?? [])
  const [recurrenceEndAt, setRecurrenceEndAt] = useState(task?.recurrence?.endAt ? toLocalInputValue(new Date(task.recurrence.endAt)) : '')
  const [status, setStatus] = useState(task?.status ?? 'open')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [confirmingDelete, setConfirmingDelete] = useState(false)

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => event.key === 'Escape' && onClose()
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  async function submit(event: FormEvent) {
    event.preventDefault(); setError('')
    if (recurrenceEnabled && !dueAt) { setError('Add a due date before setting a repeat.'); return }
    if (recurrenceEnabled && (!Number.isInteger(recurrenceInterval) || recurrenceInterval < 1 || recurrenceInterval > 365)) { setError('Repeat interval must be between 1 and 365.'); return }
    if (recurrenceEnabled && recurrenceFrequency === 'weekly' && recurrenceDays.length === 0) { setError('Choose at least one day for a weekly repeat.'); return }
    if (recurrenceEnabled && recurrenceEndAt && new Date(recurrenceEndAt) < new Date(dueAt)) { setError('The repeat end must be on or after the due date.'); return }
    setBusy(true)
    try {
      const payload = { title, priority, description, dueAt: dueAt ? new Date(dueAt).toISOString() : null, listId: listId || null, reminder: reminderEnabled && reminderAt ? { at: new Date(reminderAt).toISOString() } : null, recurrence: recurrenceEnabled ? { frequency: recurrenceFrequency, interval: recurrenceInterval, ...(recurrenceFrequency === 'weekly' ? { daysOfWeek: recurrenceDays } : {}), ...(recurrenceEndAt ? { endAt: new Date(recurrenceEndAt).toISOString() } : {}) } : null }
      const saved = task ? await updateTask(task.id, payload) : await createTask(payload)
      onSaved(saved); onClose()
    }
    catch (caught) { setError(caught instanceof Error ? caught.message : 'Could not save task.') }
    finally { setBusy(false) }
  }
  async function changeStatus() {
    if (!task) return
    setError(''); setBusy(true)
    try { const saved = await setTaskStatus(task.id, status === 'completed' ? 'open' : 'completed'); setStatus(saved.status); onSaved(saved) }
    catch (caught) { setError(caught instanceof Error ? caught.message : 'Could not update task.') }
    finally { setBusy(false) }
  }
  async function remove() {
    if (!task) return
    setError(''); setBusy(true)
    try { await deleteTask(task.id); onDeleted(task.id); onClose() }
    catch (caught) { setError(caught instanceof Error ? caught.message : 'Could not delete task.') }
    finally { setBusy(false) }
  }
  return <div className="modal-backdrop" role="presentation" onMouseDown={event => event.target === event.currentTarget && onClose()}>
    <section className="modal" role="dialog" aria-modal="true" aria-labelledby="task-modal-title">
      <div className="modal-header"><div><div className="modal-kicker"><p className="eyebrow">{task ? 'Task details' : 'New task'}</p>{task && <span className={`status-badge status-${status}`}>{status === 'completed' ? 'Completed' : 'Open'}</span>}</div><h2 id="task-modal-title">{task ? 'Keep it in view.' : 'Make space for it.'}</h2></div><button className="icon-button" aria-label="Close" onClick={onClose}>×</button></div>
      <form onSubmit={submit} className="task-form">
        <label>Title<input name="title" autoFocus value={title} onChange={event => setTitle(event.target.value)} placeholder="What needs your attention?" maxLength={200} required /></label>
        <div className="form-row"><label>When<input type="datetime-local" value={dueAt} onChange={event => setDueAt(event.target.value)} /></label><label>Priority<select value={priority} onChange={event => setPriority(event.target.value as Priority)}><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option></select></label></div>
        <label>List<select value={listId} onChange={event => setListId(event.target.value)}><option value="">No list</option>{lists.map(list => <option value={list.id} key={list.id}>{list.name}</option>)}</select></label>
        <label className="checkbox-label"><input type="checkbox" checked={reminderEnabled} onChange={event => { setReminderEnabled(event.target.checked); if (event.target.checked && !reminderAt) setReminderAt(dueAt ? toLocalInputValue(new Date(new Date(dueAt).getTime() - 15 * 60_000)) : '') }} /><span>Set a reminder</span></label>
        {reminderEnabled && <label>Reminder time<input type="datetime-local" value={reminderAt} onChange={event => setReminderAt(event.target.value)} required /></label>}
        <label className="checkbox-label"><input type="checkbox" checked={recurrenceEnabled} onChange={event => { setRecurrenceEnabled(event.target.checked); if (event.target.checked && recurrenceFrequency === 'weekly' && recurrenceDays.length === 0 && dueAt) { const day = new Date(dueAt).getDay(); setRecurrenceDays([['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'][day]]) } }} /><span>Repeat this task</span></label>
        {recurrenceEnabled && <div className="repeat-settings"><div className="form-row"><label>Repeats<select value={recurrenceFrequency} onChange={event => setRecurrenceFrequency(event.target.value as typeof recurrenceFrequency)}><option value="daily">Daily</option><option value="weekly">Weekly</option><option value="monthly">Monthly</option></select></label><label>Every<input type="number" min="1" max="365" value={recurrenceInterval} onChange={event => setRecurrenceInterval(Number(event.target.value))} /></label></div>{recurrenceFrequency === 'weekly' && <fieldset className="weekday-picker"><legend>On days</legend><div>{recurrenceDayOptions.map(([value, label]) => <button type="button" className={recurrenceDays.includes(value) ? 'day-toggle selected' : 'day-toggle'} key={value} onClick={() => setRecurrenceDays(current => current.includes(value) ? current.filter(day => day !== value) : [...current, value])} aria-label={value} aria-pressed={recurrenceDays.includes(value)}>{label}</button>)}</div></fieldset>}<label>Ends at <span className="optional">Optional</span><input type="datetime-local" value={recurrenceEndAt} onChange={event => setRecurrenceEndAt(event.target.value)} /></label></div>}
        <label>Description <span className="optional">Optional</span><textarea value={description} onChange={event => setDescription(event.target.value)} rows={3} maxLength={5000} placeholder="A little context…" /></label>
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="modal-actions"><div className="modal-danger">{task && (confirmingDelete ? <span className="delete-confirm"><span>Delete this task?</span><button type="button" className="text-button" onClick={() => setConfirmingDelete(false)} disabled={busy}>Keep</button><button type="button" className="text-button danger-button" onClick={remove} disabled={busy}>Delete</button></span> : <button type="button" className="text-button danger-button" onClick={() => setConfirmingDelete(true)} disabled={busy}>Delete</button>)}</div><button type="button" className="button button-quiet" onClick={onClose}>Cancel</button>{task && <button type="button" className="button button-quiet" onClick={changeStatus} disabled={busy}>{status === 'completed' ? 'Reopen' : 'Complete'}</button>}<button className="button button-primary" disabled={busy}>{busy ? 'Saving…' : task ? 'Save changes' : 'Save task'}</button></div>
      </form>
    </section>
  </div>
}

function Calendar({ onLogout }: { onLogout: () => void }) {
  const [month, setMonth] = useState(monthStart(new Date()))
  const [view, setView] = useState<'month' | 'week' | 'agenda'>('week')
  const [tasks, setTasks] = useState<Task[]>([])
  const [lists, setLists] = useState<TaskList[]>([])
  const [listFilter, setListFilter] = useState('')
  const [listModalOpen, setListModalOpen] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [modalDate, setModalDate] = useState<Date | null>(null)
  const [selectedTask, setSelectedTask] = useState<Task | null>(null)
  const [query, setQuery] = useState('')
  const today = new Date()
  const days = useMemo(() => calendarDays(month), [month])
  const currentWeek = useMemo(() => weekDays(month), [month])

  useEffect(() => {
    getLists().then(setLists).catch(() => setLists([]))
  }, [])

  useEffect(() => {
    let active = true
    setLoading(true); setError(''); setTasks([])
    const range = view === 'month' ? days : view === 'week' ? currentWeek : [monthStart(month), nextMonth(month)]
    const from = range[0]
    const to = view === 'agenda' ? range[1] : addDays(range[range.length - 1], 1)
    getTasks(from, to, listFilter).then(result => { if (active) setTasks(result.items) }).catch(caught => { if (active) setError(caught instanceof Error ? caught.message : 'Could not load your calendar.') }).finally(() => active && setLoading(false))
    return () => { active = false }
  }, [month, days, currentWeek, listFilter, view])

  const visibleTasks = query.trim() ? tasks.filter(task => `${task.title} ${task.description}`.toLowerCase().includes(query.toLowerCase())) : tasks
  const tasksFor = (day: Date) => visibleTasks.filter(task => { const date = taskDay(task.dueAt); return date && sameDay(date, day) })

  function saveTask(task: Task) { setTasks(current => { if (listFilter && task.listId !== listFilter) return current.filter(item => item.id !== task.id); return current.some(item => item.id === task.id) ? current.map(item => item.id === task.id ? task : item) : [...current, task] }) }
  function removeTask(id: string) { setTasks(current => current.filter(task => task.id !== id)) }
  function addList(list: TaskList) { setLists(current => [...current, list]); setListFilter(list.id) }
  function openCreate(day: Date) { setSelectedTask(null); setModalDate(day) }
  function openTask(task: Task) { setSelectedTask(task); setModalDate(taskDay(task.dueAt) ?? new Date()) }
  function movePeriod(amount: number) { setMonth(view === 'month' || view === 'agenda' ? (amount > 0 ? nextMonth(month) : previousMonth(month)) : addDays(month, amount * 7)) }
  const heading = view === 'week' ? dateRangeLabel(currentWeek[0], currentWeek[6]) : monthLabel(month)
  const agendaTasks = tasks.filter(task => { const date = taskDay(task.dueAt); return date && date.getFullYear() === month.getFullYear() && date.getMonth() === month.getMonth() })

  return <div className="app-shell">
    <a className="skip-link" href="#calendar-grid">Skip to calendar</a>
    <aside className="sidebar">
      <div className="sidebar-top"><div className="wordmark"><span className="wordmark-symbol">K</span><span>Kalendar</span></div><div className="sidebar-rule" /></div>
      <div className="sidebar-content"><p className="eyebrow sidebar-label">Your calendar</p><button className={`sidebar-link ${listFilter ? '' : 'active'}`} onClick={() => setListFilter('')}><span className="nav-dot" />All tasks</button><p className="eyebrow sidebar-label list-label">Lists</p><div className="list-nav">{lists.map(list => <button className={`sidebar-link list-link ${listFilter === list.id ? 'active' : ''}`} key={list.id} onClick={() => setListFilter(list.id)}><span className="list-dot" style={{ backgroundColor: list.color ?? '#80978a' }} />{list.name}</button>)}</div><button className="add-list-button" onClick={() => setListModalOpen(true)}><Icon name="plus" /> New list</button><p className="sidebar-note">A clear view of what’s next.<br />One day at a time.</p></div>
      <div className="sidebar-bottom"><div className="user-chip"><span className="avatar">{(localStorage.getItem('kalendar.username') ?? 'K').slice(0, 1).toUpperCase()}</span><span className="user-name">Your calendar</span><button className="icon-button sidebar-logout" onClick={onLogout} aria-label="Sign out"><Icon name="logout" /></button></div></div>
    </aside>
    <main className="calendar-main">
      <header className="topbar"><div className="topbar-title"><span className="eyebrow">Calendar</span><h1>{heading}</h1></div><div className="topbar-actions"><label className="search-field"><Icon name="search" /><span className="sr-only">Search tasks</span><input name="search" autoComplete="off" value={query} onChange={event => setQuery(event.target.value)} placeholder="Search…" /></label><button className="button button-quiet today-button" onClick={() => setMonth(today)}>Today</button><button className="button button-primary add-button" onClick={() => openCreate(today)}><Icon name="plus" /><span>Add task</span></button></div></header>
      <section className="calendar-toolbar" aria-label="Calendar controls"><div className="month-nav"><button className="icon-button" onClick={() => movePeriod(-1)} aria-label="Previous period"><Icon name="chevron-left" /></button><button className="icon-button" onClick={() => movePeriod(1)} aria-label="Next period"><Icon name="chevron-right" /></button></div><div className="calendar-toolbar-right"><div className="view-switcher" role="group" aria-label="Calendar view"><button className={view === 'month' ? 'selected' : ''} onClick={() => setView('month')}>Month</button><button className={view === 'week' ? 'selected' : ''} onClick={() => setView('week')}>Week</button><button className={view === 'agenda' ? 'selected' : ''} onClick={() => setView('agenda')}>Agenda</button></div><select className="mobile-list-filter" aria-label="Filter by list" value={listFilter} onChange={event => setListFilter(event.target.value)}><option value="">All lists</option>{lists.map(list => <option key={list.id} value={list.id}>{list.name}</option>)}</select><p className="calendar-hint">{tasks.length ? `${tasks.length} task${tasks.length === 1 ? '' : 's'} this view` : 'Your calendar at a glance'}</p></div></section>
      {error && <div className="state-message error-state" role="alert"><strong>We couldn’t load this {view === 'week' ? 'week' : view === 'agenda' ? 'agenda' : 'month'}.</strong><span>{error}</span><button className="text-button" onClick={() => setMonth(new Date(month))}>Try again</button></div>}
      {view === 'month' && <section id="calendar-grid" className={`calendar-grid ${loading ? 'is-loading' : ''}`} role="grid" aria-label={`${monthLabel(month)} calendar`} aria-busy={loading}>
        {['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(day => <div className="weekday" key={day}>{day.slice(0, 3)}<span className="weekday-full">{day.slice(3)}</span></div>)}
        {days.map(day => { const dayTasks = tasksFor(day); const inMonth = day.getMonth() === month.getMonth(); return <div className={`day-cell ${inMonth ? '' : 'outside-month'} ${sameDay(day, today) ? 'today' : ''}`} key={day.toISOString()} role="gridcell" tabIndex={0} onClick={() => openCreate(day)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); openCreate(day) } }} aria-label={`${weekdayLabel(day)}, ${dayLabel(day)}${dayTasks.length ? `, ${dayTasks.length} tasks` : ''}`}><span className="day-number">{dayLabel(day)}</span><span className="day-tasks">{dayTasks.slice(0, 4).map(task => <button className={`task-pill priority-${task.priority} ${task.status === 'completed' ? 'is-complete' : ''}`} style={{ borderLeftColor: lists.find(list => list.id === task.listId)?.color ?? undefined }} title={`${task.title}${task.reminder?.at ? ' • reminder set' : ''}`} key={task.id} onClick={event => { event.stopPropagation(); openTask(task) }}><span className="task-mark" />{task.title}{task.reminder?.at && <span className="reminder-mark" aria-label="Reminder set">•</span>}</button>)}{dayTasks.length > 4 && <span className="more-tasks">+{dayTasks.length - 4} more</span>}</span></div> })}
      </section>}
      {view === 'week' && <div id="calendar-grid" className={loading ? 'is-loading' : ''} aria-busy={loading}><WeekView days={currentWeek} tasksFor={tasksFor} lists={lists} onCreate={openCreate} onOpen={openTask} /></div>}
      {view === 'agenda' && <div id="calendar-grid" className={loading ? 'is-loading' : ''} aria-busy={loading}><AgendaView tasks={query.trim() ? agendaTasks.filter(task => `${task.title} ${task.description}`.toLowerCase().includes(query.toLowerCase())) : agendaTasks} lists={lists} emptyDate={monthStart(month)} onCreate={openCreate} onOpen={openTask} /></div>}
      {loading && <div className="loading-line" aria-live="polite">Loading your calendar…</div>}
      {!loading && !error && query.trim() && visibleTasks.length === 0 && <div className="state-message empty-state"><strong>No tasks match “{query}”.</strong><span>Try a different word or clear your search.</span><button className="text-button" onClick={() => setQuery('')}>Clear search</button></div>}
      {!loading && !error && !query.trim() && tasks.length === 0 && view === 'month' && <div className="state-message empty-state"><strong>This month is still open.</strong><span>Add a task to give the days ahead a shape.</span><button className="text-button" onClick={() => openCreate(monthStart(month))}>Add your first task</button></div>}
    </main>
    {modalDate && <TaskModal task={selectedTask ?? undefined} lists={lists} selectedDate={modalDate} onClose={() => { setModalDate(null); setSelectedTask(null) }} onSaved={saveTask} onDeleted={removeTask} />}
    {listModalOpen && <ListModal onClose={() => setListModalOpen(false)} onCreated={addList} />}
  </div>
}

export default function App() {
  const [authenticated, setAuthenticated] = useState(Boolean(getToken()))
  useEffect(() => {
    const expireSession = () => { setToken(null); localStorage.removeItem('kalendar.username'); setAuthenticated(false) }
    window.addEventListener('kalendar:unauthorized', expireSession)
    return () => window.removeEventListener('kalendar:unauthorized', expireSession)
  }, [])
  function logout() { setToken(null); localStorage.removeItem('kalendar.username'); setAuthenticated(false) }
  return authenticated ? <Calendar onLogout={logout} /> : <AuthScreen onAuthenticated={() => setAuthenticated(true)} />
}
