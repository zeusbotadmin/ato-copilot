import axios from 'axios';
import { attachAuthInterceptor } from '../features/auth/interceptors';
import { getMsalInstance, DEFAULT_API_SCOPES } from '../features/auth/msalInstance';

// ─── V1 API Client ──────────────────────────────────────────────────────────

const v1Client = axios.create({
  baseURL: '/api/v1',
  headers: { 'Content-Type': 'application/json' },
});

// Feature 051 T053: MSAL bearer injection (silent renewal + 401 retry).
attachAuthInterceptor(v1Client, getMsalInstance, DEFAULT_API_SCOPES);

// ─── Types ──────────────────────────────────────────────────────────────────

export interface SapFamilySummary {
  family: string;
  controlCount: number;
  customerCount: number;
  inheritedCount: number;
  methods: string[];
}

export interface SapResponse {
  sapId: string;
  systemId: string;
  assessmentId?: string;
  title: string;
  status: string;
  format: string;
  baselineLevel: string;
  contentHash?: string;
  totalControls: number;
  customerControls: number;
  inheritedControls: number;
  sharedControls: number;
  stigBenchmarkCount: number;
  controlsWithObjectives: number;
  evidenceGaps: number;
  familySummaries: SapFamilySummary[];
  generatedAt: string;
  finalizedAt?: string;
  warnings: string[];
}

// ─── API Functions ──────────────────────────────────────────────────────────

const BASE = '/systems';

export async function generateSap(systemId: string) {
  const { data } = await v1Client.post<SapResponse>(`${BASE}/${systemId}/sap`);
  return data;
}

export async function getLatestSap(systemId: string) {
  const { data } = await v1Client.get<SapResponse>(`${BASE}/${systemId}/sap`);
  return data;
}

export async function finalizeSap(systemId: string, sapId: string) {
  const { data } = await v1Client.post<SapResponse>(`${BASE}/${systemId}/sap/${sapId}/finalize`);
  return data;
}
