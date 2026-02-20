import { Injectable } from '@angular/core';
import { AgentSession } from '../models/session.model';
import { ClaudeMessage } from '../models/claude-message.model';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly baseUrl = `${environment.apiUrl}/api/sessions`;

  async getSessions(): Promise<AgentSession[]> {
    const res = await fetch(this.baseUrl);
    return res.json();
  }

  async getSession(id: string): Promise<AgentSession> {
    const res = await fetch(`${this.baseUrl}/${id}`);
    return res.json();
  }

  async getContent(id: string): Promise<{ messages?: ClaudeMessage[] }> {
    const res = await fetch(`${this.baseUrl}/${id}/content`);
    return res.json();
  }

  async createSession(name: string, workingDirectory: string, model?: string): Promise<AgentSession> {
    const res = await fetch(this.baseUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, workingDirectory, model: model ?? null }),
    });
    return res.json();
  }

  async resumeSession(id: string): Promise<AgentSession> {
    const res = await fetch(`${this.baseUrl}/${id}/resume`, { method: 'POST' });
    return res.json();
  }

  async renameSession(id: string, name: string): Promise<void> {
    await fetch(`${this.baseUrl}/${id}/name`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    });
  }

  async stopSession(id: string): Promise<void> {
    await fetch(`${this.baseUrl}/${id}/stop`, { method: 'POST' });
  }

  async removeSession(id: string): Promise<void> {
    await fetch(`${this.baseUrl}/${id}`, { method: 'DELETE' });
  }
}
