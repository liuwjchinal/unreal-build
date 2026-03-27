import type {
  BuildDetailDto,
  BuildEventEnvelope,
  BuildLogSnapshotDto,
  BuildScheduleDetailDto,
  BuildScheduleRunResultDto,
  BuildScheduleSummaryDto,
  BuildSummaryDto,
  ImportProjectsResult,
  ProjectConfigDto,
  ProjectSummaryDto,
  QueueBuildRequest,
  UpsertBuildScheduleRequest,
  UpsertProjectRequest,
  ValidationProblemDetails,
} from '../types/api'

const API_BASE = import.meta.env.VITE_API_BASE ?? ''

function getDownloadBase() {
  if (API_BASE) {
    return API_BASE
  }

  if (typeof window === 'undefined') {
    return ''
  }

  const { protocol, hostname, port, origin } = window.location
  if (port === '5173') {
    return `${protocol}//${hostname}:5080`
  }

  return origin
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
    ...init,
  })

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as ValidationProblemDetails | null
    const details = body?.errors
      ? Object.entries(body.errors)
          .map(([field, messages]) => `${field}: ${messages.join('；')}`)
          .join('\n')
      : body?.message || body?.title
    throw new Error(details || `请求失败: ${response.status}`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return (await response.json()) as T
}

export const api = {
  getProjects() {
    return request<ProjectSummaryDto[]>('/api/projects')
  },
  getProjectConfig(id: string) {
    return request<ProjectConfigDto>(`/api/projects/${id}/config`)
  },
  async exportProjects() {
    const response = await fetch(`${API_BASE}/api/projects/export`)
    if (!response.ok) {
      throw new Error(`导出失败: ${response.status}`)
    }
    return response.blob()
  },
  importProjects(payload: UpsertProjectRequest[]) {
    return request<ImportProjectsResult>('/api/projects/import', {
      method: 'POST',
      body: JSON.stringify(payload),
    })
  },
  createProject(payload: UpsertProjectRequest) {
    return request<ProjectSummaryDto>('/api/projects', {
      method: 'POST',
      body: JSON.stringify(payload),
    })
  },
  updateProject(id: string, payload: UpsertProjectRequest) {
    return request<ProjectSummaryDto>(`/api/projects/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    })
  },
  deleteProject(id: string) {
    return request<void>(`/api/projects/${id}`, {
      method: 'DELETE',
    })
  },
  getSchedules() {
    return request<BuildScheduleSummaryDto[]>('/api/schedules')
  },
  getSchedule(id: string) {
    return request<BuildScheduleDetailDto>(`/api/schedules/${id}`)
  },
  createSchedule(payload: UpsertBuildScheduleRequest) {
    return request<BuildScheduleDetailDto>('/api/schedules', {
      method: 'POST',
      body: JSON.stringify(payload),
    })
  },
  updateSchedule(id: string, payload: UpsertBuildScheduleRequest) {
    return request<BuildScheduleDetailDto>(`/api/schedules/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    })
  },
  deleteSchedule(id: string) {
    return request<void>(`/api/schedules/${id}`, {
      method: 'DELETE',
    })
  },
  toggleSchedule(id: string) {
    return request<BuildScheduleSummaryDto>(`/api/schedules/${id}/toggle`, {
      method: 'POST',
    })
  },
  runScheduleNow(id: string) {
    return request<BuildScheduleRunResultDto>(`/api/schedules/${id}/run-now`, {
      method: 'POST',
    })
  },
  getBuilds(projectId?: string) {
    const search = new URLSearchParams()
    if (projectId) {
      search.set('projectId', projectId)
    }
    search.set('limit', '100')
    return request<BuildSummaryDto[]>(`/api/builds?${search.toString()}`)
  },
  queueBuild(payload: QueueBuildRequest) {
    return request<BuildDetailDto>('/api/builds', {
      method: 'POST',
      body: JSON.stringify(payload),
    })
  },
  cancelBuild(id: string) {
    return request<BuildDetailDto>(`/api/builds/${id}/cancel`, {
      method: 'POST',
    })
  },
  getBuild(id: string) {
    return request<BuildDetailDto>(`/api/builds/${id}`)
  },
  getBuildLog(id: string, tailLines?: number) {
    const search = new URLSearchParams()
    if (tailLines && tailLines > 0) {
      search.set('tailLines', String(tailLines))
    }

    const suffix = search.size > 0 ? `?${search.toString()}` : ''
    return request<BuildLogSnapshotDto>(`/api/builds/${id}/log${suffix}`)
  },
  createBuildEventSource(id: string) {
    return new EventSource(`${API_BASE}/api/builds/${id}/events`)
  },
  toDownloadUrl(path?: string | null) {
    return path ? `${getDownloadBase()}${path}` : null
  },
}

export function parseBuildEvent(event: MessageEvent<string>) {
  const raw = JSON.parse(event.data) as Record<string, unknown>

  return {
    eventType:
      ((raw.eventType ?? raw.EventType ?? event.type) as BuildEventEnvelope['eventType']) ?? 'heartbeat',
    buildId: String(raw.buildId ?? raw.BuildId ?? ''),
    payload: ((raw.payload ?? raw.Payload ?? {}) as Record<string, unknown>) ?? {},
    occurredAtUtc: String(raw.occurredAtUtc ?? raw.OccurredAtUtc ?? new Date().toISOString()),
  }
}
