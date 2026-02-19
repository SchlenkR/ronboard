import { Injectable, signal, computed } from '@angular/core';
import { SignalRService } from './signalr.service';
import { AgentSession, SessionMode } from '../models/session.model';
import { ClaudeMessage } from '../models/claude-message.model';

@Injectable({ providedIn: 'root' })
export class SessionService {
  readonly sessions = signal<AgentSession[]>([]);
  readonly activeSessionId = signal<string | null>(null);
  readonly connected = signal(false);

  // Stream mode messages for active session
  readonly messages = signal<ClaudeMessage[]>([]);

  readonly activeSession = computed(() => {
    const id = this.activeSessionId();
    return this.sessions().find(s => s.id === id) ?? null;
  });

  private previousSessionId: string | null = null;

  constructor(private signalr: SignalRService) {
    this.signalr.connected$.subscribe(async (connected) => {
      this.connected.set(connected);
      if (connected) {
        const sessions = await this.signalr.getSessions();
        this.sessions.set(sessions);
      }
    });

    this.signalr.sessionCreated$.subscribe(session => {
      this.sessions.update(list =>
        list.some(s => s.id === session.id) ? list : [...list, session]);
    });

    this.signalr.sessionStopped$.subscribe(id => {
      this.sessions.update(list =>
        list.map(s => s.id === id ? { ...s, status: 'stopped' as const } : s));
    });

    this.signalr.sessionRemoved$.subscribe(id => {
      this.sessions.update(list => list.filter(s => s.id !== id));
      if (this.activeSessionId() === id) {
        this.activeSessionId.set(null);
        this.messages.set([]);
      }
    });

    this.signalr.sessionEnded$.subscribe(id => {
      this.sessions.update(list =>
        list.map(s => s.id === id ? { ...s, status: 'stopped' as const } : s));
    });

    // Stream mode: collect messages
    this.signalr.streamMessage$.subscribe(({ sessionId, message }) => {
      if (sessionId === this.activeSessionId()) {
        this.messages.update(msgs => [...msgs, message]);
      }
    });

    this.signalr.streamHistory$.subscribe(history => {
      this.messages.set(history);
    });
  }

  async selectSession(sessionId: string): Promise<void> {
    if (this.previousSessionId) {
      await this.signalr.leaveSession(this.previousSessionId);
    }
    this.activeSessionId.set(sessionId);
    this.messages.set([]);
    await this.signalr.joinSession(sessionId);
    this.previousSessionId = sessionId;
  }

  async createSession(name: string, workingDirectory: string,
    mode: SessionMode = 'terminal'): Promise<void> {
    const session = await this.signalr.createSession(name, workingDirectory, mode);
    this.sessions.update(list =>
      list.some(s => s.id === session.id) ? list : [...list, session]);
    await this.selectSession(session.id);
  }

  // Terminal mode: raw keystrokes
  async sendInput(data: string): Promise<void> {
    const id = this.activeSessionId();
    if (id) await this.signalr.sendInput(id, data);
  }

  // Stream mode: text message
  async sendMessage(message: string): Promise<void> {
    const id = this.activeSessionId();
    if (id) await this.signalr.sendMessage(id, message);
  }

  async stopSession(sessionId: string): Promise<void> {
    await this.signalr.stopSession(sessionId);
  }

  async removeSession(sessionId: string): Promise<void> {
    await this.signalr.removeSession(sessionId);
  }
}
