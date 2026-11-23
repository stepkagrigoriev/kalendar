const monthFormatter = new Intl.DateTimeFormat('en-US', { month: 'long', year: 'numeric' })
const weekdayFormatter = new Intl.DateTimeFormat('en-US', { weekday: 'short' })
const dayFormatter = new Intl.DateTimeFormat('en-US', { day: 'numeric' })
const timeFormatter = new Intl.DateTimeFormat('en-US', { hour: 'numeric', minute: '2-digit' })
const agendaDateFormatter = new Intl.DateTimeFormat('en-US', { weekday: 'long', month: 'long', day: 'numeric' })

export function monthLabel(date: Date) { return monthFormatter.format(date) }
export function weekdayLabel(date: Date) { return weekdayFormatter.format(date) }
export function dayLabel(date: Date) { return dayFormatter.format(date) }
export function timeLabel(date: Date) { return timeFormatter.format(date) }
export function agendaDateLabel(date: Date) { return agendaDateFormatter.format(date) }

export function monthStart(date: Date) { return new Date(date.getFullYear(), date.getMonth(), 1) }
export function nextMonth(date: Date) { return new Date(date.getFullYear(), date.getMonth() + 1, 1) }
export function previousMonth(date: Date) { return new Date(date.getFullYear(), date.getMonth() - 1, 1) }
export function addDays(date: Date, amount: number) { const result = new Date(date); result.setDate(result.getDate() + amount); return result }
export function weekStart(date: Date) { return addDays(new Date(date.getFullYear(), date.getMonth(), date.getDate()), -((date.getDay() + 6) % 7)) }
export function weekDays(date: Date) { const start = weekStart(date); return Array.from({ length: 7 }, (_, index) => addDays(start, index)) }
export function dateRangeLabel(start: Date, end: Date) {
  const sameMonth = start.getMonth() === end.getMonth() && start.getFullYear() === end.getFullYear()
  return sameMonth ? `${monthLabel(start)} ${dayLabel(start)}–${dayLabel(end)}` : `${monthLabel(start)} ${dayLabel(start)} – ${monthLabel(end)} ${dayLabel(end)}`
}

export function calendarDays(month: Date) {
  const start = monthStart(month)
  const firstGridDay = new Date(start)
  const mondayOffset = (start.getDay() + 6) % 7
  firstGridDay.setDate(start.getDate() - mondayOffset)
  return Array.from({ length: 42 }, (_, index) => {
    const day = new Date(firstGridDay)
    day.setDate(firstGridDay.getDate() + index)
    return day
  })
}

export function sameDay(a: Date, b: Date) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()
}

export function toLocalInputValue(date: Date) {
  const offset = date.getTimezoneOffset() * 60_000
  return new Date(date.getTime() - offset).toISOString().slice(0, 16)
}

export function taskDay(taskDate: string | null) { return taskDate ? new Date(taskDate) : null }
