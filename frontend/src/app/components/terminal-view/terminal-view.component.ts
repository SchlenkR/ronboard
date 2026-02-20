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
    :host { display: flex; flex-direction: column; width: 100%; height: 100%; min-height: 0; overflow: hidden; }
    .terminal-container { flex: 1; width: 100%; min-height: 0; }
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
        // Delay fit until layout has settled after display change
        this.deferFit();
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
      scrollback: 10000,
    });

    this.fitAddon = new FitAddon();
    this.terminal.loadAddon(this.fitAddon);
    this.terminal.open(container);

    this.terminal.onData((data) => {
      this.sessionService.sendInput(data);
    });

    // ResizeObserver for window/container resizes
    this.resizeObserver = new ResizeObserver((entries) => {
      const { width, height } = entries[0].contentRect;
      if (width > 0 && height > 0) {
        this.fitAddon?.fit();
      }
    });
    this.resizeObserver.observe(container);
  }

  /** Fit terminal after layout settles (handles display:none → visible transition) */
  private deferFit(): void {
    // Use requestAnimationFrame + setTimeout to ensure layout is complete
    requestAnimationFrame(() => {
      setTimeout(() => {
        this.fitAddon?.fit();
        this.terminal?.focus();
      }, 0);
    });
  }

  ngOnDestroy(): void {
    this.subs.forEach(s => s.unsubscribe());
    this.resizeObserver?.disconnect();
    this.terminal?.dispose();
  }
}
