import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

export type TipoFechamento = 'ganho' | 'perda';

export interface ResultadoFechamento {
  tipo: TipoFechamento;
  /** Preenchido só em 'ganho'. */
  valor: number;
  /** Preenchido só em 'perda'. */
  motivo: string;
}

/** A CONFIRMAÇÃO DE FECHAMENTO — venda ganha ou perdida.
 *
 *  ===================== UM COMPONENTE, DUAS PORTAS =====================
 *  O vendedor pode arrastar o card para a coluna "Venda" NO KANBAN ou clicar em "Venda fechada"
 *  NO DETALHE. As duas abrem este mesmo modal e chamam o mesmo endpoint.
 *
 *  Isso espelha a decisão do backend: `POST /api/funil/{id}/mover` RECUSA a etapa de ganho com
 *  409, justamente para não existir um segundo caminho que grave diferente. Se cada porta
 *  tivesse seu próprio formulário, um deles acabaria esquecendo de exigir o valor — e existiria
 *  contato na coluna Venda sem `ganho_em`, invisível para o dashboard.
 *  =====================================================================
 *
 *  Não emite requisição: devolve o que o usuário preencheu e quem abriu decide o que chamar.
 *  É o que permite o kanban desfazer o movimento otimista quando o vendedor cancela. */
@Component({
  selector: 'app-modal-fechamento',
  imports: [FormsModule],
  template: `
    <div class="overlay" (click)="cancelar.emit()">
      <div class="modal" (click)="$event.stopPropagation()">
        <div class="cartao-topo">
          <h2>{{ ehGanho() ? 'Venda fechada' : 'Cliente perdido' }}</h2>
        </div>

        <div class="modal-corpo">
          <p class="quem fraco">{{ contatoNome() }}</p>

          @if (ehGanho()) {
            <div class="campo">
              <label for="valor">Valor da venda</label>
              <input id="valor" type="number" min="0" step="0.01" inputmode="decimal"
                     placeholder="0,00" [ngModel]="valor()"
                     (ngModelChange)="valor.set($event)" />
              <div class="dica">
                O valor entra no faturamento do mês e no total da coluna do funil.
              </div>
            </div>
          } @else {
            <div class="campo">
              <label for="motivo">Motivo da perda</label>
              <input id="motivo" type="text" maxlength="200"
                     placeholder="Ex.: achou caro, comprou do concorrente"
                     [ngModel]="motivo()" (ngModelChange)="motivo.set($event)" />
              <div class="dica">
                Registrado junto da etapa em que a negociação parou.
              </div>
            </div>
          }

          @if (erro()) { <div class="erro">{{ erro() }}</div> }

          <div class="linha acoes">
            <span class="espaco"></span>
            <button type="button" class="btn btn-neutro" (click)="cancelar.emit()"
                    [disabled]="salvando()">Cancelar</button>
            <button type="button" class="btn" [class.btn-perigo]="!ehGanho()"
                    (click)="confirmar()" [disabled]="salvando() || !valido()">
              {{ salvando() ? 'Salvando…' : (ehGanho() ? 'Registrar venda' : 'Marcar como perdido') }}
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .quem { margin: 0 0 14px; font-size: 14px; }
    .acoes { margin-top: 18px; gap: 8px; }
    .erro { margin-top: 12px; }
  `]
})
export class ModalFechamento {
  tipo = input.required<TipoFechamento>();
  contatoNome = input('');
  salvando = input(false);
  /** Mensagem vinda da API (o modal fica aberto para a pessoa corrigir). */
  erro = input('');

  confirmado = output<ResultadoFechamento>();
  cancelar = output<void>();

  valor = signal<number | null>(null);
  motivo = signal('');

  ehGanho = computed(() => this.tipo() === 'ganho');

  valido = computed(() =>
    this.ehGanho() ? (this.valor() ?? 0) > 0 : this.motivo().trim().length > 0);

  confirmar() {
    if (!this.valido()) return;
    this.confirmado.emit({
      tipo: this.tipo(),
      valor: this.valor() ?? 0,
      motivo: this.motivo().trim()
    });
  }
}
