import { Component, inject } from '@angular/core';
import { ToastServico } from './toast.servico';

/** A pilha de toasts. Fica no shell e nas páginas públicas — qualquer lugar que possa
 *  receber aviso sem clique do usuário. */
@Component({
  selector: 'app-toast',
  standalone: true,
  template: `
    <div class="pilha" role="status" aria-live="polite">
      @for (t of servico.toasts(); track t.id) {
        <div class="toast toast-{{ t.tipo }}">
          <span>{{ t.texto }}</span>
          <button type="button" class="fechar" (click)="servico.fechar(t.id)"
                  aria-label="Fechar aviso">×</button>
        </div>
      }
    </div>
  `,
  styles: [`
    .pilha {
      position: fixed; z-index: 100; right: 20px; bottom: 20px;
      display: flex; flex-direction: column; gap: 8px; max-width: 380px;
    }
    .toast {
      display: flex; align-items: flex-start; gap: 10px;
      padding: 11px 14px; border-radius: 10px; font-size: 14px;
      background: var(--branco); color: var(--texto);
      border: 1px solid var(--linha);
      box-shadow: 0 8px 24px rgba(20, 67, 47, .16);
      animation: entra .16s ease-out;
    }
    .toast-sucesso { border-left: 3px solid var(--urgencia-baixa); }
    .toast-erro { border-left: 3px solid var(--alerta); color: var(--alerta); }
    .toast-info { border-left: 3px solid var(--verde-2); }
    .fechar {
      background: none; border: 0; padding: 0; margin-left: auto;
      color: var(--texto-fraco); font-size: 18px; line-height: 1; cursor: pointer;
    }
    @keyframes entra { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: none; } }
  `]
})
export class ToastPilha {
  servico = inject(ToastServico);
}
