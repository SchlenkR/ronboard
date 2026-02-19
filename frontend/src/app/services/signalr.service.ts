import { Injectable, OnDestroy } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from '@microsoft/signalr';
import { Subject, BehaviorSubject } from 'rxjs';
import { AgentSession, SessionMode } from '../models/session.model';
import { ClaudeMessage } from '../models/claude-message.model';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class SignalRService implements OnDestroy {
  private connection: HubConnection;

  // Session lifecycle
  readonly sessionCreated$ = new Subject<AgentSession>();
  readonly sessionStopped$ = new Subject<string>();
  readonly sessionRemoved$ = new Subject<string>();
  readonly sessionEnded$ = new Subject<string>();
  readonly connected$ = new BehaviorSubject<boolean>(false);

  // Terminal mode events
  readonly terminalOutput$ = new Subject<{ sessionId: string; data: string }>();
  readonly terminalHistory$ = new Subject<string>();

  // Stream mode events
  readonly streamMessage$ = new Subject<{ sessionId: string; message: ClaudeMessage }>();
  readonly streamHistory$ = new Subject<ClaudeMessage[]>();

  constructor() {
    this.connection = new HubConnectionBuilder()
      .withUrl(`${environment.apiUrl}/hubs/session`, {
        skipNegotiation: true,
        transport: HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Information)
      .build();

    this.registerHandlers();
    this.start();
  }

  private registerHandlers(): void {
    this.connection.on('SessionCreated', (session: AgentSession) =>
      this.sessionCreated$.next(session));
    this.connection.on('SessionStopped', (id: string) =>
      this.sessionStopped$.next(id));
    this.connection.on('SessionRemoved', (id: string) =>
      this.sessionRemoved$.next(id));
    this.connection.on('SessionEnded', (id: string) =>
      this.sessionEnded$.next(id));

    // Terminal mode
    this.connection.on('TerminalOutput', (sessionId: string, data: string) =>
      this.terminalOutput$.next({ sessionId, data }));
    this.connection.on('TerminalHistory', (history: string) =>
      this.terminalHistory$.next(history));

    // Stream mode
    this.connection.on('StreamMessage', (sessionId: string, msg: ClaudeMessage) =>
      this.streamMessage$.next({ sessionId, message: msg }));
    this.connection.on('StreamHistory', (messages: ClaudeMessage[]) =>
      this.streamHistory$.next(messages));
  }

  private async start(): Promise<void> {
    try {
      await this.connection.start();
      this.connected$.next(true);
    } catch (err) {
      console.error('SignalR connection failed:', err);
      setTimeout(() => this.start(), 3000);
    }
  }

  async getSessions(): Promise<AgentSession[]> {
    return this.connection.invoke<AgentSession[]>('GetSessions');
  }

  async createSession(name: string, workingDirectory: string,
    mode: SessionMode = 'terminal', model?: string): Promise<AgentSession> {
    return this.connection.invoke<AgentSession>('CreateSession', name, workingDirectory, mode, model ?? null);
  }

  // Terminal mode: raw keystrokes
  async sendInput(sessionId: string, data: string): Promise<void> {
    await this.connection.invoke('SendInput', sessionId, data);
  }

  // Stream mode: text message
  async sendMessage(sessionId: string, message: string): Promise<void> {
    await this.connection.invoke('SendMessage', sessionId, message);
  }

  async joinSession(sessionId: string): Promise<void> {
    await this.connection.invoke('JoinSession', sessionId);
  }

  async leaveSession(sessionId: string): Promise<void> {
    await this.connection.invoke('LeaveSession', sessionId);
  }

  async stopSession(sessionId: string): Promise<void> {
    await this.connection.invoke('StopSession', sessionId);
  }

  async removeSession(sessionId: string): Promise<void> {
    await this.connection.invoke('RemoveSession', sessionId);
  }

  ngOnDestroy(): void {
    this.connection.stop();
  }
}
