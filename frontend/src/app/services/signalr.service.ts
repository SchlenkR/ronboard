import { Injectable, OnDestroy } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from '@microsoft/signalr';
import { Subject, BehaviorSubject } from 'rxjs';
import { AgentSession } from '../models/session.model';
import { ClaudeMessage } from '../models/claude-message.model';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class SignalRService implements OnDestroy {
  private connection: HubConnection;

  // Server → Client events
  readonly sessionCreated$ = new Subject<AgentSession>();
  readonly sessionStopped$ = new Subject<string>();
  readonly sessionRemoved$ = new Subject<string>();
  readonly sessionEnded$ = new Subject<string>();
  readonly sessionResumed$ = new Subject<AgentSession>();
  readonly sessionRenamed$ = new Subject<{ sessionId: string; name: string }>();
  readonly connected$ = new BehaviorSubject<boolean>(false);
  readonly reconnected$ = new Subject<void>();

  readonly sessionActivity$ = new Subject<{ sessionId: string; state: string }>();
  readonly sessionStatusChanged$ = new Subject<{ sessionId: string; status: string }>();

  readonly terminalOutput$ = new Subject<{ sessionId: string; data: string }>();
  readonly terminalHistory$ = new Subject<string>();
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

    this.connection.onreconnected(() => {
      console.log('SignalR reconnected');
      this.connected$.next(true);
      this.reconnected$.next();
    });

    this.connection.onclose(() => {
      this.connected$.next(false);
    });

    this.registerHandlers();
    this.start();
  }

  private registerHandlers(): void {
    this.connection.on('SessionCreated', (s: AgentSession) => this.sessionCreated$.next(s));
    this.connection.on('SessionStopped', (id: string) => this.sessionStopped$.next(id));
    this.connection.on('SessionRemoved', (id: string) => this.sessionRemoved$.next(id));
    this.connection.on('SessionEnded', (id: string) => this.sessionEnded$.next(id));
    this.connection.on('SessionResumed', (s: AgentSession) => this.sessionResumed$.next(s));
    this.connection.on('SessionRenamed', (id: string, name: string) => this.sessionRenamed$.next({ sessionId: id, name }));

    this.connection.on('SessionActivity', (id: string, state: string) => this.sessionActivity$.next({ sessionId: id, state }));
    this.connection.on('SessionStatusChanged', (id: string, status: string) => this.sessionStatusChanged$.next({ sessionId: id, status }));

    this.connection.on('TerminalOutput', (id: string, data: string) => this.terminalOutput$.next({ sessionId: id, data }));
    this.connection.on('TerminalHistory', (history: string) => this.terminalHistory$.next(history));
    this.connection.on('StreamMessage', (id: string, msg: ClaudeMessage) => this.streamMessage$.next({ sessionId: id, message: msg }));
    this.connection.on('StreamHistory', (msgs: ClaudeMessage[]) => this.streamHistory$.next(msgs));
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

  // Real-time bidirectional I/O
  async sendInput(sessionId: string, data: string): Promise<void> {
    await this.connection.invoke('SendInput', sessionId, data);
  }

  async sendMessage(sessionId: string, message: string): Promise<void> {
    await this.connection.invoke('SendMessage', sessionId, message);
  }

  // Group management (needs ConnectionId)
  async joinSession(sessionId: string): Promise<void> {
    await this.connection.invoke('JoinSession', sessionId);
  }

  async leaveSession(sessionId: string): Promise<void> {
    await this.connection.invoke('LeaveSession', sessionId);
  }

  ngOnDestroy(): void {
    this.connection.stop();
  }
}
