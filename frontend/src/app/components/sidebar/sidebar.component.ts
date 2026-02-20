import { Component, inject, computed, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { SessionService } from '../../services/session.service';
import { AgentSession } from '../../models/session.model';

interface SessionGroup {
  label: string;
  sessions: AgentSession[];
}

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.css',
})
export class SidebarComponent {
  sessionService = inject(SessionService);
  sessionSelected = output<void>();

  groupedSessions = computed<SessionGroup[]>(() => {
    const sessions = this.sessionService.sessions();
    const now = new Date();
    const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const yesterday = new Date(today.getTime() - 86400000);
    const sevenDaysAgo = new Date(today.getTime() - 7 * 86400000);

    const groups: Record<string, AgentSession[]> = {
      'Today': [],
      'Yesterday': [],
      'Last 7 Days': [],
      'Older': [],
    };

    const sorted = [...sessions].sort((a, b) =>
      new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());

    for (const session of sorted) {
      const date = new Date(session.createdAt);
      if (date >= today) groups['Today'].push(session);
      else if (date >= yesterday) groups['Yesterday'].push(session);
      else if (date >= sevenDaysAgo) groups['Last 7 Days'].push(session);
      else groups['Older'].push(session);
    }

    return Object.entries(groups)
      .filter(([, s]) => s.length > 0)
      .map(([label, sessions]) => ({ label, sessions }));
  });

  async quickCreate(): Promise<void> {
    await this.sessionService.createSession('Untitled', '~/');
    this.sessionSelected.emit();
  }

  async select(sessionId: string): Promise<void> {
    await this.sessionService.selectSession(sessionId);
    this.sessionSelected.emit();
  }

  async remove(event: Event, sessionId: string): Promise<void> {
    event.stopPropagation();
    await this.sessionService.removeSession(sessionId);
  }

  getActivity(sessionId: string): string {
    return this.sessionService.activityStates()[sessionId] ?? 'idle';
  }

  isUnread(sessionId: string): boolean {
    return this.sessionService.unreadSessionIds().has(sessionId);
  }

  getDotState(session: AgentSession): string {
    const activity = this.getActivity(session.id);
    if (activity === 'busy' || activity === 'tool_use') return 'active';
    if (this.isUnread(session.id)) return 'unread';
    if (session.status === 'stopped') return 'paused';
    if (session.status === 'error') return 'error';
    return 'default';
  }
}
