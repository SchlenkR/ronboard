import {
  Component, inject, effect, viewChild, ElementRef,
  OnDestroy, AfterViewInit,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { SessionService } from '../../services/session.service';
import { SignalRService } from '../../services/signalr.service';

@Component({
  selector: 'app-terminal-view',
  standalone: true,
  template: `<div class="terminal-container" #terminalContainer></div>`,
  styles: [`
    :host { display: block; width: 100%; height: 100%; overflow: hidden; }
    .terminal-container { width: 100%; height: 100%; }
  `],
})
export class TerminalViewComponent implements AfterViewInit, OnDestroy {
  private sessionService = inject(SessionService);
  private signalr = inject(SignalRService);
  private terminalContainer = viewChild<ElementRef>('terminalContainer');

  private terminal: Terminal | null = null;
  private fitAddon: FitAddon | null = null;
  private subs: Subscription[] = [];
  private resizeObserver: ResizeObserver | null = null;
  private currentSessionId: string | null = null;

  constructor() {
    effect(() => {
      const session = this.sessionService.activeSession();
      const newId = session?.id ?? null;

      if (newId && newId !== this.currentSessionId) {
        this.currentSessionId = newId;
        this.terminal?.clear();
        this.terminal?.reset();
        setTimeout(() => {
          this.fitAddon?.fit();
          this.terminal?.focus();
        }, 10);
      } else if (!newId) {
        this.currentSessionId = null;
      }
    });
  }

  ngAfterViewInit(): void {
    this.initTerminal();

    this.subs.push(
      this.signalr.terminalOutput$.subscribe(({ sessionId, data }) => {
        if (sessionId === this.sessionService.activeSessionId()) {
          this.terminal?.write(data);
        }
      }),
      this.signalr.terminalHistory$.subscribe((history) => {
        this.terminal?.write(history);
      }),
    );
  }

  private initTerminal(): void {
    const container = this.terminalContainer()?.nativeElement;
    if (!container) return;

    this.terminal = new Terminal({
      cursorBlink: true,
      fontSize: 13,
      fontFamily: "'JetBrains Mono', 'Cascadia Code', 'Fira Code', 'Consolas', monospace",
      theme: {
        background: '#1e1e1e',
        foreground: '#d4d4d4',
        cursor: '#d4d4d4',
        selectionBackground: '#264f78',
      },
      cols: 120,
      rows: 40,
      scrollback: 10000,
    });

    this.fitAddon = new FitAddon();
    this.terminal.loadAddon(this.fitAddon);
    this.terminal.open(container);

    this.terminal.onData((data) => {
      this.sessionService.sendInput(data);
    });

    this.resizeObserver = new ResizeObserver(() => {
      setTimeout(() => this.fitAddon?.fit(), 0);
    });
    this.resizeObserver.observe(container);
  }

  ngOnDestroy(): void {
    this.subs.forEach(s => s.unsubscribe());
    this.resizeObserver?.disconnect();
    this.terminal?.dispose();
  }
}
