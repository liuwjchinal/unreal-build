import type { BuildStatus } from '../types/api'

const labelMap: Record<BuildStatus, string> = {
  Queued: '排队中',
  Running: '构建中',
  Succeeded: '已完成',
  Failed: '失败',
  Interrupted: '中断',
}

function renderStatusIcon(status: BuildStatus) {
  if (status === 'Running') {
    return <span className="status-icon spinner" aria-hidden="true" />
  }

  if (status === 'Succeeded') {
    return (
      <span className="status-icon success-check" aria-hidden="true">
        ✔
      </span>
    )
  }

  return null
}

export function BuildStatusBadge({ status }: { status?: BuildStatus | null | unknown }) {
  if (!status || typeof status !== 'string') {
    return <span className="status-pill queued">状态未知</span>
  }

  const typedStatus = status as BuildStatus
  return (
    <span className={`status-pill ${typedStatus.toLowerCase()}`}>
      <span>{labelMap[typedStatus] ?? typedStatus}</span>
      {renderStatusIcon(typedStatus)}
    </span>
  )
}
