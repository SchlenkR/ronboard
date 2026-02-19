import { Component } from '@angular/core';
import { SidebarComponent } from './components/sidebar/sidebar.component';
import { SessionViewComponent } from './components/session-view/session-view.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [SidebarComponent, SessionViewComponent],
  template: `
    <div class="app-layout">
      <app-sidebar class="sidebar" />
      <app-session-view class="main" />
    </div>
  `,
  styles: [`
    .app-layout {
      display: flex;
      height: 100vh;
      background: #1e1e1e;
      color: #d4d4d4;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    }
    .sidebar {
      width: 280px;
      min-width: 280px;
      border-right: 1px solid #333;
      background: #252526;
    }
    .main {
      flex: 1;
      display: flex;
      flex-direction: column;
      min-width: 0;
    }
  `]
})
export class AppComponent {}
