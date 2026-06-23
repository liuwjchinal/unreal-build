import type { AndroidPackagingMode, BuildPlatform } from '../types/api'

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

export function formatPlatform(platform: BuildPlatform) {
  switch (platform) {
    case 'Android':
      return 'Android'
    case 'OpenHarmony':
      return 'OpenHarmony'
    default:
      return 'Windows'
  }
}

export function formatAndroidPackagingMode(mode?: AndroidPackagingMode | string | null) {
  switch (mode) {
    case 'ExternalFilesIoStore':
      return 'External Files IoStore'
    case 'SplitObb':
      return 'Split OBB'
    case 'DataInsideApk':
      return 'Data Inside APK'
    default:
      return '-'
  }
}

export function formatBytes(bytes?: number | null) {
  if (bytes == null) {
    return '-'
  }

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unitIndex = 0
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex++
  }

  return `${value.toFixed(unitIndex === 0 ? 0 : 1)} ${units[unitIndex]}`
}
