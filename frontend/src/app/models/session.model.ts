export type SessionStatus = 'starting' | 'running' | 'idle' | 'stopped' | 'error';

export interface AgentSession {
  id: string;
  number: number;
  name: string;
  workingDirectory: string;
  status: SessionStatus;
  createdAt: string;
  lastUsedAt: string;
}
