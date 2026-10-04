import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

export interface ResultadoCancelamento {
  /** `null` = "registrei errado". Preenchido = "o cliente desistiu", e o texto é o motivo. */
  motivo: string | null;
}

/** POR QUE A VENDA ESTÁ SENDO CANCELADA (CAN-1).
 *
 *  ===================== UM BOTÃO SERVIA A DUAS COISAS =====================
 *  Cancelar atendia duas situações e tratava as duas igual:
 *
 *    "registrei errado"   — valor trocado, cliente duplicado. Nada se perdeu.
 *    "o cliente desistiu" — a venda existiu, foi contada, e ele voltou atrás. É perda.
 *
 *  O sistema não perguntava, então não sabia. E sempre devolvia o contato ao quadro como negócio
 *  aberto — certo para a primeira, errado para a segunda: alguém acaba ligando para cobrar um
 *  cliente que já foi embora.
 *
 *  ⚠️ CADA OPÇÃO DIZ O QUE VAI ACONTECER, logo abaixo dela. Não é texto de ajuda: as duas mexem
 *  em coisas diferentes (uma conta como perda e segura o card, a outra não), e quem escolhe
 *  precisa saber disso ANTES de clicar — depois as duas são indistinguíveis na tela.
 *  =======================================================================
 *
 *  ⚠️ O PADRÃO É "REGISTREI ERRADO", que é o comportamento que sempre existiu. Quem não quiser
 *  mudar nada confirma direto e segue igual — a opção nova não pode custar um passo a mais a quem
 *  já usava o produto.
 *
 *  Não emite requisição: devolve o que a pessoa escolheu e quem abriu decide o que chamar. Mesma
 *  regra do `modal-fechamento`, e pelo mesmo motivo. */
@Component({
  selector: 'app-modal-cancelamento',
  imports: [FormsModule],
  template: `
    <div class="overlay" (click)="cancelar.emit()">
      <div class="modal" (click)="$event.stopPropagation()">
        <div class="cartao-topo">
          <h2>Cancelar venda</h2>
        </div>

        <div class="modal-corpo">
          <p class="quem fraco">{{ contatoNome() }} · {{ valorFormatado() }}</p>

          <div class="escolha">
            <label class="opcao">
              <input type="radio" name="porque" [checked]="!foiPerda()"
                     (change)="foiPerda.set(false)" />
              <span>
                <strong>Registrei errado</strong>
                <small class="fraco">
                  Valor trocado, cliente duplicado, lancei duas vezes. O valor sai do faturamento e
                  o contato volta para o funil.
                </small>
              </span>
            </label>

            <label class="opcao">
              <input type="radio" name="porque" [checked]="foiPerda()"
                     (change)="foiPerda.set(true)" />
              <span>
                <strong>O cliente desistiu</strong>
                <small class="fraco">
                  Fechou e voltou atrás. Conta como perda, com o valor desta venda, e o contato
                  <strong>não</strong> volta para o funil.
                </small>
              </span>
            </label>
          </div>

          @if (foiPerda()) {
            <div class="campo">
              <label for="motivo-cancelamento">Por que ele desistiu?</label>
              <input id="motivo-cancelamento" type="text" maxlength="200"
                     placeholder="Ex.: achou caro, prazo de entrega, comprou do concorrente"
                     [ngModel]="motivo()" (ngModelChange)="motivo.set($event)" />
              <div class="dica">
                Vira uma linha no relatório de perdas, com o valor da venda ao lado.
              </div>
            </div>
          }

          @if (erro()) { <div class="erro">{{ erro() }}</div> }

          <div class="linha acoes">
            <span class="espaco"></span>
            <button type="button" class="btn btn-neutro" (click)="cancelar.emit()"
                    [disabled]="salvando()">Voltar</button>
            <button type="button" class="btn btn-perigo"
                    (click)="confirmar()" [disabled]="salvando() || !valido()">
              {{ salvando() ? 'Cancelando…' : 'Cancelar venda' }}
            </button>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .quem { margin: 0 0 14px; font-size: 14px; }
    .escolha { display: flex; flex-direction: column; gap: 12px; }
    .opcao {
      display: flex; gap: 10px; align-items: flex-start; cursor: pointer; margin: 0;
      padding: 12px; border: 1px solid var(--linha); border-radius: 8px;
    }
    .opcao:has(input:checked) { border-color: var(--verde); background: var(--creme); }

    /* ⚠️ O GLOBAL FAZ "input { width: 100%; padding: 9px 12px }", pensado para campo de texto.
       Num RADIO isso vira um bloco de largura inteira: a bolinha centraliza, o texto ao lado é
       empurrado para fora do modal, e a altura explode — foi exatamente o que apareceu na tela.
       Nenhuma outra tela do produto usa radio, então o global nunca teve motivo para prever um, e
       reescrever a regra global por causa deste modal mexeria em todo formulário do sistema.
       A exceção fica aqui, onde o caso é.

       ⚠️ E NADA DE CRASE DENTRO DESTE BLOCO: "styles" é um template literal, e a crase do
       comentário fecha a string. O compilador reclama de "styles at position 1", que não diz
       nada sobre a causa. */
    .opcao input[type="radio"] {
      width: auto; padding: 0; margin: 2px 0 0; flex: 0 0 auto;
      accent-color: var(--verde);
    }
    /* "min-width: 0" para o texto QUEBRAR em vez de esticar a caixa — sem ele um item flex não
       encolhe abaixo do próprio conteúdo, e a frase longa vira rolagem horizontal. */
    .opcao > span { flex: 1 1 auto; min-width: 0; }
    .opcao strong { display: block; font-size: 14px; font-weight: 600; color: var(--texto); }
    .opcao small { display: block; margin-top: 3px; font-size: 12px; line-height: 1.45; }
    .campo { margin-top: 16px; }
    .acoes { margin-top: 18px; gap: 8px; }
    .erro { margin-top: 12px; }
  `]
})
export class ModalCancelamento {
  contatoNome = input('');
  /** Só para a pessoa confirmar que está desfazendo a venda certa. */
  valorFormatado = input('');
  salvando = input(false);
  /** Mensagem vinda da API — o modal fica aberto para a pessoa corrigir. */
  erro = input('');

  confirmado = output<ResultadoCancelamento>();
  cancelar = output<void>();

  foiPerda = signal(false);
  motivo = signal('');

  /** ⚠️ "Registrei errado" é SEMPRE válido; "o cliente desistiu" exige o porquê.
   *
   *  É a mesma assimetria do backend, e de propósito: a opção que conta como perda vira uma linha
   *  num relatório, e linha de perda sem motivo não ajuda ninguém a decidir nada. */
  valido = computed(() => !this.foiPerda() || this.motivo().trim().length > 0);

  confirmar() {
    if (!this.valido()) return;
    this.confirmado.emit({ motivo: this.foiPerda() ? this.motivo().trim() : null });
  }
}
