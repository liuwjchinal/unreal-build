import type {
  BuildDetailDto,
  BuildEventEnvelope,
  BuildLogSnapshotDto,
  BuildStageLogListDto,
  BuildStageLogSnapshotDto,
  BuildScheduleDetailDto,
  BuildScheduleRunResultDto,
  BuildScheduleSummaryDto,
  BuildSummaryDto,
  ImportProjectsResult,
  ProjectConfigDto,
  ProjectSummaryDto,
  QueueBuildRequest,
  UbaAgentConfigDto,
  UpsertBuildScheduleRequest,
  UpsertProjectRequest,
  ValidationProblemDetails,
} from '../types/api'

const API_BASE = (import.meta.env.VITE_API_BASE ?? '').replace(/\/$/, '')
const DIRECT_BACKEND_ORIGIN = (__LOCAL_BUILD_BACKEND_ORIGIN__ ?? '').replace(/\/$/, '')

function isLoopbackHostname(hostname: string) {
  const normalized = hostname.trim().toLowerCase()
  return normalized === 'localhost' || normalized === '127.0.0.1' || normalized === '::1' || normalized === '[::1]'
}

function resolveRequestBase() {
  if (API_BASE) {
    return API_BASE
  }

  if (typeof window === 'undefined') {
    return DIRECT_BACKEND_ORIGIN
  }

  return isLoopbackHostname(window.location.hostname) ? DIRECT_BACKEND_ORIGIN : ''
}

const REQUEST_BASE = resolveRequestBase()

function getDownloadBase() {
  if (REQUEST_BASE) {
    return REQUEST_BASE
  }

  if (typeof window === 'undefined') {
    return ''
  }

  return window.location.origin
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${REQUEST_BASE}${path}`, {
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
          .map(([field, messages]) => `${field}: ${messages.join(', ')}`)
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
    const response = await fetch(`${REQUEST_BASE}/api/projects/export`)
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
  getBuildStageLogs(id: string) {
    return request<BuildStageLogListDto>(`/api/builds/${id}/stage-logs`)
  },
  getBuildStageLog(id: string, stageKey: string, tailLines?: number) {
    const search = new URLSearchParams()
    if (tailLines && tailLines > 0) {
      search.set('tailLines', String(tailLines))
    }

    const suffix = search.size > 0 ? `?${search.toString()}` : ''
    return request<BuildStageLogSnapshotDto>(`/api/builds/${id}/stage-logs/${encodeURIComponent(stageKey)}${suffix}`)
  },
  getUbaAgentConfig() {
    return request<UbaAgentConfigDto>('/api/uba-agent/config')
  },
  getUbaAgentPackageUrl(projectId?: string | null) {
    const search = new URLSearchParams()
    if (projectId) {
      search.set('projectId', projectId)
    }

    const suffix = search.size > 0 ? `?${search.toString()}` : ''
    return `${REQUEST_BASE}/api/uba-agent/package${suffix}`
  },
  createBuildEventSource(id: string) {
    return new EventSource(`${REQUEST_BASE}/api/builds/${id}/events`)
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
