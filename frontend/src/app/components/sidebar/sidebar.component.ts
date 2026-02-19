import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { SessionService } from '../../services/session.service';
import { SessionMode } from '../../models/session.model';

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.css',
})
export class SidebarComponent {
  sessionService = inject(SessionService);
  showNewDialog = false;
  selectedMode: SessionMode = 'terminal';

  async create(nameInput: HTMLInputElement, dirInput: HTMLInputElement): Promise<void> {
    const name = nameInput.value.trim();
    const dir = dirInput.value.trim();
    if (name && dir) {
      await this.sessionService.createSession(name, dir, this.selectedMode);
      nameInput.value = '';
      this.showNewDialog = false;
      this.selectedMode = 'terminal';
    }
  }

  async remove(event: Event, sessionId: string): Promise<void> {
    event.stopPropagation();
    await this.sessionService.removeSession(sessionId);
  }
}
