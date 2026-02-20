import { Component, inject, effect, viewChild, ElementRef, AfterViewInit, computed } from '@angular/core';
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
export class StreamViewComponent implements AfterViewInit {
  sessionService = inject(SessionService);
  private messageArea = viewChild<ElementRef>('messageArea');
  private inputRef = viewChild<ElementRef>('input');
  private wasActive = false;

  readonly isActive = computed(() => {
    const id = this.sessionService.activeSessionId();
    if (!id) return false;
    const state = this.sessionService.activityStates()[id];
    return state === 'busy' || state === 'tool_use';
  });

  readonly activityLabel = computed(() => {
    const id = this.sessionService.activeSessionId();
    if (!id) return '';
    const state = this.sessionService.activityStates()[id];
    if (state === 'tool_use') return 'Running tool';
    if (state === 'busy') return 'Thinking';
    return '';
  });

  constructor() {
    // Auto-scroll on new messages
    effect(() => {
      this.sessionService.messages();
      const el = this.messageArea()?.nativeElement;
      if (el) {
        setTimeout(() => el.scrollTop = el.scrollHeight);
      }
    });

    // Focus input when session changes or LLM finishes
    effect(() => {
      const sessionId = this.sessionService.activeSessionId();
      const active = this.isActive();
      // Session switched → focus
      if (sessionId) {
        this.focusInput();
      }
      // LLM just finished → focus
      if (this.wasActive && !active) {
        this.focusInput();
      }
      this.wasActive = active;
    });
  }

  ngAfterViewInit(): void {
    this.focusInput();
  }

  async send(input: HTMLTextAreaElement): Promise<void> {
    const text = input.value.trim();
    if (text) {
      await this.sessionService.sendMessage(text);
      input.value = '';
      this.autoResize(input);
      input.focus();
    }
  }

  onKeydown(event: KeyboardEvent, input: HTMLTextAreaElement): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send(input);
    }
  }

  autoResize(textarea: HTMLTextAreaElement): void {
    textarea.style.height = 'auto';
    textarea.style.height = textarea.scrollHeight + 'px';
  }

  private focusInput(): void {
    setTimeout(() => this.inputRef()?.nativeElement?.focus(), 0);
  }
}
