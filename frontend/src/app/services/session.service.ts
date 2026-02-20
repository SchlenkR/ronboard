import { Injectable, signal, computed } from '@angular/core';
import { SignalRService } from './signalr.service';
import { ApiService } from './api.service';
import { AgentSession, SessionMode } from '../models/session.model';
import { ClaudeMessage } from '../models/claude-message.model';

@Injectable({ providedIn: 'root' })
export class SessionService {
  readonly sessions = signal<AgentSession[]>([]);
  readonly activeSessionId = signal<string | null>(null);
  readonly connected = signal(false);
  readonly messages = signal<ClaudeMessage[]>([]);

  readonly activeSession = computed(() => {
    const id = this.activeSessionId();
    return this.sessions().find(s => s.id === id) ?? null;
  });

  private previousSessionId: string | null = null;

  constructor(private signalr: SignalRService, private api: ApiService) {
    // Initial load via HTTP
    this.signalr.connected$.subscribe(async (connected) => {
      this.connected.set(connected);
      if (connected) {
        this.sessions.set(await this.api.getSessions());
      }
    });

    // Real-time updates via SignalR
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

    this.signalr.sessionResumed$.subscribe(session => {
      this.sessions.update(list =>
        list.map(s => s.id === session.id ? session : s));
    });

    this.signalr.sessionRenamed$.subscribe(({ sessionId, name }) => {
      this.sessions.update(list =>
        list.map(s => s.id === sessionId ? { ...s, name } : s));
    });

    // Stream mode: collect real-time messages
    this.signalr.streamMessage$.subscribe(({ sessionId, message }) => {
      if (sessionId === this.activeSessionId()) {
        this.messages.update(msgs => [...msgs, message]);
      }
    });

    this.signalr.streamHistory$.subscribe(history => {
      this.messages.set(history);
    });
  }

  // SignalR: join/leave groups, resume sessions
  async selectSession(sessionId: string): Promise<void> {
    if (this.previousSessionId) {
      await this.signalr.leaveSession(this.previousSessionId);
    }
    this.activeSessionId.set(sessionId);
    this.messages.set([]);
    await this.signalr.joinSession(sessionId);
    this.previousSessionId = sessionId;

    const session = this.sessions().find(s => s.id === sessionId);
    if (session?.status === 'stopped') {
      await this.api.resumeSession(sessionId);
    }
  }

  // HTTP: create session (starts broadcast loops on backend)
  async createSession(name: string, workingDirectory: string, mode: SessionMode = 'terminal'): Promise<void> {
    const session = await this.api.createSession(name, workingDirectory, mode);
    this.sessions.update(list =>
      list.some(s => s.id === session.id) ? list : [...list, session]);
    await this.selectSession(session.id);
  }

  // SignalR: real-time I/O
  async sendInput(data: string): Promise<void> {
    const id = this.activeSessionId();
    if (id) await this.signalr.sendInput(id, data);
  }

  async sendMessage(message: string): Promise<void> {
    const id = this.activeSessionId();
    if (!id) return;

    // Add user message locally so it's visible immediately
    const userMsg: ClaudeMessage = {
      index: this.messages().length,
      timestamp: new Date().toISOString(),
      type: 'user_message',
      rawJson: { text: message },
    };
    this.messages.update(msgs => [...msgs, userMsg]);

    await this.signalr.sendMessage(id, message);
  }

  // HTTP: actions that don't need real-time
  async stopSession(sessionId: string): Promise<void> {
    await this.api.stopSession(sessionId);
  }

  async removeSession(sessionId: string): Promise<void> {
    await this.api.removeSession(sessionId);
  }
}
