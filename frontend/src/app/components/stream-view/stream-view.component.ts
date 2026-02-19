import { Component, inject, effect, viewChild, ElementRef, AfterViewInit } from '@angular/core';
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

  constructor() {
    effect(() => {
      this.sessionService.messages();
      const el = this.messageArea()?.nativeElement;
      if (el) {
        setTimeout(() => el.scrollTop = el.scrollHeight);
      }
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
      input.focus();
    }
  }

  onKeydown(event: KeyboardEvent, input: HTMLTextAreaElement): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send(input);
    }
  }

  private focusInput(): void {
    setTimeout(() => this.inputRef()?.nativeElement?.focus(), 50);
  }
}
