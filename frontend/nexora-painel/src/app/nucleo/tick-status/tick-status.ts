import { Component, input } from '@angular/core';

export type StatusEnvio = 'pendente' | 'enviado' | 'entregue' | 'lido' | 'falha';

/** Tick de status de mensagem ENVIADA, no estilo do WhatsApp — mas com a NOSSA paleta
 *  (verde/âmbar, nunca o azul do WhatsApp). SVG inline (sem emoji), reutilizado no balão do
 *  chat e na prévia da lista.
 *
 *  🕐 pendente (na fila) · ✓ enviado · ✓✓ entregue (cinza) · ✓✓ lido (âmbar, destaque) ·
 *  ⚠ falha. Cor via CSS por estado; `escuro` ajusta para o balão de fundo escuro. */
@Component({
  selector: 'app-tick-status',
  standalone: true,
  template: `
    <span class="tick tick-{{ estado() }}" [class.escuro]="escuro()"
          [title]="titulo()" [attr.aria-label]="titulo()">
      @switch (estado()) {
        @case ('pendente') {
          <svg viewBox="0 0 16 14" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round">
            <circle cx="8" cy="7" r="5.4" /><path d="M8 4 V7 l2 1.4" />
          </svg>
        }
        @case ('enviado') {
          <svg viewBox="0 0 18 14" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <path d="M4 7.5 L7.5 11 L14 3.5" />
          </svg>
        }
        @default {
          @if (estado() === 'falha') {
            <svg viewBox="0 0 16 14" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round" stroke-linecap="round">
              <path d="M8 1.5 L15 12.5 H1 Z" /><path d="M8 5.5 V9" /><circle cx="8" cy="11" r=".2" stroke-width="1.4" />
            </svg>
          } @else {
            <!-- entregue e lido: dois ticks (a cor distingue) -->
            <svg viewBox="0 0 20 14" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <path d="M2 7.5 L5.5 11 L12 3.5" /><path d="M8 7.5 L11.5 11 L18 3.5" />
            </svg>
          }
        }
      }
    </span>
  `,
  styles: [`
    .tick { display: inline-flex; vertical-align: middle; line-height: 0; color: var(--texto-fraco); }
    .tick svg { width: 16px; height: 12px; display: block; }
    /* fundo claro (prévia da lista) */
    .tick-entregue { color: var(--verde-2); }
    .tick-lido { color: var(--urgencia-media); }
    .tick-falha { color: var(--alerta); }
    /* fundo escuro (balão de saída do chat) — mantém contraste; 'lido' em âmbar, não azul */
    .tick.escuro { color: rgba(255,255,255,.7); }
    .tick.escuro.tick-entregue { color: rgba(255,255,255,.95); }
    .tick.escuro.tick-lido { color: #E8C77A; }
    .tick.escuro.tick-falha { color: #F0B49B; }
  `]
})
export class TickStatus {
  estado = input.required<StatusEnvio>();
  titulo = input('');
  escuro = input(false);
}

/** O ACK numérico do WhatsApp vira estado visual.
 *  0=erro, 1=enviado(pendente no servidor), 2=servidor, 3=entregue, 4=lido. */
export function estadoDoAck(
  ack: number | null, enviadaEm: string | null, erro: string | null
): StatusEnvio {
  if (erro && !enviadaEm) return 'falha';
  switch (ack) {
    case 0: return 'falha';
    case 4: return 'lido';
    case 3: return 'entregue';
    case 2: return 'enviado';
    case 1: return 'enviado';
    default: return enviadaEm ? 'enviado' : 'pendente';
  }
}

export function rotuloAck(ack: number | null): string {
  switch (ack) {
    case 0: return 'erro';
    case 1: return 'enviando';
    case 2: return 'enviada';
    case 3: return 'entregue';
    case 4: return 'lida';
    default: return '';
  }
}
