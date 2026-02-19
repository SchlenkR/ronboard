import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ClaudeMessage } from '../../models/claude-message.model';

@Component({
  selector: 'app-message-renderer',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './message-renderer.component.html',
  styleUrl: './message-renderer.component.css',
})
export class MessageRendererComponent {
  @Input({ required: true }) message!: ClaudeMessage;

  /** Unwrap stream_event → event, otherwise use rawJson directly */
  private get eventType(): string {
    if (this.message.type === 'stream_event') {
      return this.message.rawJson?.event?.type ?? 'unknown';
    }
    return this.message.type;
  }

  private get eventData(): any {
    if (this.message.type === 'stream_event') {
      return this.message.rawJson?.event ?? {};
    }
    return this.message.rawJson ?? {};
  }

  get isTextDelta(): boolean {
    if (this.eventType !== 'content_block_delta') return false;
    return this.eventData?.delta?.type === 'text_delta';
  }

  get textDelta(): string {
    return this.eventData?.delta?.text ?? '';
  }

  get isToolUse(): boolean {
    if (this.eventType !== 'content_block_start') return false;
    return this.eventData?.content_block?.type === 'tool_use';
  }

  get toolName(): string {
    return this.eventData?.content_block?.name ?? 'unknown';
  }

  get isInputJsonDelta(): boolean {
    if (this.eventType !== 'content_block_delta') return false;
    return this.eventData?.delta?.type === 'input_json_delta';
  }

  get inputJsonDelta(): string {
    return this.eventData?.delta?.partial_json ?? '';
  }

  get isToolResult(): boolean {
    return this.eventType === 'content_block_start'
      && this.eventData?.content_block?.type === 'tool_result';
  }

  get isResult(): boolean {
    return this.message.type === 'result';
  }

  get resultText(): string {
    return this.message.rawJson?.result ?? '';
  }

  get isContentBlockStop(): boolean {
    return this.eventType === 'content_block_stop';
  }

  get isMessageLifecycle(): boolean {
    const t = this.eventType;
    return t === 'message_start' || t === 'message_stop' || t === 'message_delta';
  }

  get isPing(): boolean {
    return this.eventType === 'ping';
  }

  get isHidden(): boolean {
    return this.isContentBlockStop || this.isMessageLifecycle || this.isPing;
  }

  get fallbackLabel(): string {
    if (this.message.type === 'stream_event') {
      return `stream_event → ${this.eventType}`;
    }
    return this.message.type;
  }
}
