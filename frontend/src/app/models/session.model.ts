export type SessionStatus = 'starting' | 'running' | 'idle' | 'stopped' | 'error';
export type SessionMode = 'terminal' | 'stream';

export interface AgentSession {
  id: string;
  number: number;
  name: string;
  workingDirectory: string;
  status: SessionStatus;
  mode: SessionMode;
  createdAt: string;
  lastUsedAt: string;
}
