import { Component, inject, effect, viewChild, ElementRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { SessionService } from '../../services/session.service';
import { MessageRendererComponent } from '../message-renderer/message-renderer.component';

@Component({
  selector: 'app-stream-view',
  standalone: true,
  imports: [CommonModule, MessageRendererComponent],
  templateUrl: './stream-view.component.html',
  styleUrl: './stream-view.component.css',
})
export class StreamViewComponent {
  sessionService = inject(SessionService);
  private messageArea = viewChild<ElementRef>('messageArea');

  constructor() {
    effect(() => {
      this.sessionService.messages();
      const el = this.messageArea()?.nativeElement;
      if (el) {
        setTimeout(() => el.scrollTop = el.scrollHeight);
      }
    });
  }

  async send(input: HTMLTextAreaElement): Promise<void> {
    const text = input.value.trim();
    if (text) {
      await this.sessionService.sendMessage(text);
      input.value = '';
    }
  }

  onKeydown(event: KeyboardEvent, input: HTMLTextAreaElement): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send(input);
    }
  }
}
