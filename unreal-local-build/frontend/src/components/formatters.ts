export function formatUtc(value?: string | null) {
  if (!value) {
    return '-'
  }

  return new Date(value).toLocaleString('zh-CN', { hour12: false })
}

export function formatDuration(seconds?: number | null) {
  if (seconds == null) {
    return '-'
  }

  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const remainingSeconds = seconds % 60

  if (hours > 0) {
    return `${hours}h ${minutes}m ${remainingSeconds}s`
  }

  if (minutes > 0) {
    return `${minutes}m ${remainingSeconds}s`
  }

  return `${remainingSeconds}s`
}

export function parseTextAreaList(value: string) {
  return value
    .split(/\r?\n|,/)
    .map((item) => item.trim())
    .filter(Boolean)
}

export function joinList(value: string[] | undefined | null) {
  return (value ?? []).join('\n')
}

export function formatSvnRevision(value?: string | null) {
  if (!value) {
    return 'SVN HEAD'
  }

  return value.toUpperCase() === 'HEAD'
    ? 'SVN HEAD'
    : value.toLowerCase().startsWith('r')
      ? `SVN ${value}`
      : `SVN r${value}`
}
