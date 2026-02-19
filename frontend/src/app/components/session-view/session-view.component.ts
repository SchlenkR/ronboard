import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { SessionService } from '../../services/session.service';
import { TerminalViewComponent } from '../terminal-view/terminal-view.component';
import { StreamViewComponent } from '../stream-view/stream-view.component';

@Component({
  selector: 'app-session-view',
  standalone: true,
  imports: [CommonModule, TerminalViewComponent, StreamViewComponent],
  templateUrl: './session-view.component.html',
  styleUrl: './session-view.component.css',
})
export class SessionViewComponent {
  sessionService = inject(SessionService);

  async stop(): Promise<void> {
    const session = this.sessionService.activeSession();
    if (session) {
      await this.sessionService.stopSession(session.id);
    }
  }
}
