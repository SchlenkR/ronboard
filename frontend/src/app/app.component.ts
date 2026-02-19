import { Component, signal, HostListener } from '@angular/core';
import { SidebarComponent } from './components/sidebar/sidebar.component';
import { SessionViewComponent } from './components/session-view/session-view.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [SidebarComponent, SessionViewComponent],
  template: `
    <div class="app-layout">
      <button class="hamburger" (click)="toggleSidebar()">&#9776;</button>

      @if (sidebarOpen()) {
        <div class="sidebar-overlay" (click)="closeSidebar()"></div>
      }

      <app-sidebar class="sidebar" [class.open]="sidebarOpen()"
                   (sessionSelected)="closeSidebar()" />
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
    .hamburger {
      display: none;
      position: fixed;
      top: 10px;
      left: 10px;
      z-index: 1001;
      background: #2d2d2d;
      border: 1px solid #555;
      color: #e0e0e0;
      font-size: 20px;
      padding: 6px 10px;
      border-radius: 6px;
      cursor: pointer;
    }
    .hamburger:hover {
      background: #3d3d3d;
    }
    .sidebar-overlay {
      display: none;
    }

    @media (max-width: 768px) {
      .hamburger {
        display: block;
      }
      .sidebar {
        position: fixed;
        left: -280px;
        top: 0;
        bottom: 0;
        z-index: 1000;
        transition: left 0.25s ease;
        box-shadow: none;
      }
      .sidebar.open {
        left: 0;
        box-shadow: 4px 0 12px rgba(0, 0, 0, 0.5);
      }
      .sidebar-overlay {
        display: block;
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.5);
        z-index: 999;
      }
      .main {
        width: 100%;
      }
    }
  `]
})
export class AppComponent {
  sidebarOpen = signal(false);

  toggleSidebar(): void {
    this.sidebarOpen.update(v => !v);
  }

  closeSidebar(): void {
    this.sidebarOpen.set(false);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeSidebar();
  }
}
