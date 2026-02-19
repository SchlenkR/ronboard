export type SessionStatus = 'starting' | 'running' | 'idle' | 'stopped' | 'error';
export type SessionMode = 'terminal' | 'stream';

export interface AgentSession {
  id: string;
  name: string;
  workingDirectory: string;
  status: SessionStatus;
  mode: SessionMode;
  createdAt: string;
}
