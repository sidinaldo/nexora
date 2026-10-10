import { Component, computed, effect, input, output, signal } from '@angular/core';
import { EtiquetaDto } from '../modelos';
import { textoSobre } from '../cor';

/** ===================== ONDE A ETIQUETA VAI FICAR (BUG-XX) =====================
 *  A pessoa e cada negociação aberta dela têm etiquetas próprias, e nenhuma é copiada para a outra.
 *  Com `alvos`, o seletor pergunta onde marcar e diz onde ela vai aparecer; cada alvo guarda o seu
 *  rascunho, e o confirmar devolve só os que mudaram. Sem `alvos`, é o seletor de sempre. */
export interface AlvoEtiquetas {
  chave: string;
  rotulo: string;
  /** Onde ela aparece — o que a pessoa precisa saber ANTES de marcar. */
  aviso: string;
  atuais: EtiquetaDto[];
}

export interface EtiquetasNoAlvo { chave: string; ids: number[]; }

/** O SELETOR DE ETIQUETAS — um só, para as três telas.
 *
 *  ===================== SEGUE O `modal-fechamento`, E NÃO POR ESTILO =====================
 *  Aquele componente é usado pela caixa, pelo contato e pelo funil — exatamente as mesmas três
 *  telas —, e as cinco regras dele resolvem problemas que este teria de resolver de novo:
 *
 *  1. **Não emite requisição.** Devolve os ids escolhidos e quem abriu decide o que chamar. É o
 *     que permite ao quadro desfazer um estado otimista quando o servidor recusa.
 *  2. **`salvando` e `erro` são INPUTS**, não estado interno: o modal fica aberto para a pessoa
 *     corrigir, e quem é dono do ciclo da requisição é quem abriu.
 *  3. `.overlay` fecha no clique, `.modal` faz `stopPropagation`.
 *  4. `valido()` guarda o confirmar.
 *  5. ⚠️ **As etiquetas atuais chegam por `effect`, não por `computed`.** É a regra que mais
 *     importa aqui: a lista do contato vem de uma requisição, e se ela demorar não pode atropelar
 *     o que a pessoa já marcou. O `effect` só semeia enquanto ninguém tocou.
 *  ======================================================================================
 *
 *  ⚠️ MODAL, e não popover: `paginas.celular.spec.ts` varre toda tela e reprova `position: fixed`
 *  ou `sticky`, com `.overlay` como única exceção registrada. */
@Component({
  selector: 'app-seletor-etiquetas',
  template: `
    <div class="overlay" (click)="cancelar.emit()" (keydown)="aoTeclar($event)">
      <div class="modal" (click)="$event.stopPropagation()"
           role="dialog" aria-modal="true" aria-labelledby="titulo-etiquetas">
        <div class="cartao-topo">
          <h2 id="titulo-etiquetas">Etiquetas de {{ contatoNome() }}</h2>
        </div>

        <div class="modal-corpo">
          @if (vocabulario().length === 0) {
            <!-- Sem vocabulário não há o que escolher, e mandar a pessoa "criar primeiro" sem
                 dizer onde seria deixá-la procurando. -->
            <div class="vazio">
              <p>Nenhuma etiqueta cadastrada ainda.</p>
              <p class="fraco">
                Quem cria o vocabulário é o dono, em Configuração → Etiquetas.
              </p>
            </div>
          } @else {
            @if (alvos().length > 1) {
              <div class="abas alvos" role="group" aria-label="Onde marcar">
                @for (a of alvos(); track a.chave) {
                  <button type="button" class="aba" [class.ativa]="alvo() === a.chave"
                          [attr.aria-pressed]="alvo() === a.chave" [disabled]="salvando()"
                          (click)="escolherAlvo(a.chave)">{{ a.rotulo }}</button>
                }
              </div>
            }
            @if (avisoDoAlvo(); as aviso) { <p class="onde">{{ aviso }}</p> }

            <p class="quem fraco">
              Toque para marcar ou desmarcar. Até {{ maximo() }} por contato.
            </p>

            <div class="grade-etiquetas">
              @for (e of vocabulario(); track e.id) {
                <button type="button" class="chip chip-botao"
                        [class.marcada]="marcadas().has(e.id)"
                        [style.background]="marcadas().has(e.id) ? e.cor : 'transparent'"
                        [style.color]="marcadas().has(e.id) ? textoSobre(e.cor) : null"
                        [style.borderColor]="e.cor"
                        [disabled]="salvando() || (cheio() && !marcadas().has(e.id))"
                        [attr.aria-pressed]="marcadas().has(e.id)"
                        (click)="alternar(e.id)">
                  {{ e.nome }}
                </button>
              }
            </div>

            @if (cheio()) {
              <p class="dica">
                Limite de {{ maximo() }} atingido. Desmarque uma para escolher outra.
              </p>
            }
          }

          @if (erro()) { <div class="erro">{{ erro() }}</div> }

          <div class="acoes">
            <button type="button" class="btn" (click)="confirmar()"
                    [disabled]="salvando() || vocabulario().length === 0">
              {{ salvando() ? 'Salvando…' : 'Salvar' }}
            </button>
            <button type="button" class="btn btn-neutro" (click)="cancelar.emit()"
                    [disabled]="salvando()">Cancelar</button>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .quem { margin: 0 0 14px; font-size: 13px; }
    .alvos { margin: 0 0 12px; flex-wrap: wrap; }
    .onde {
      margin: 0 0 12px; font-size: 13px; padding: 8px 10px; border-radius: 8px;
      background: var(--urgencia-baixa-fundo); color: var(--verde-2);
    }
    .grade-etiquetas { display: flex; flex-wrap: wrap; gap: 8px; }
    /* Borda na cor da etiqueta mesmo desmarcada: é o que deixa a cor visível antes de escolher,
       sem o chip apagado parecer desabilitado. */
    .chip-botao { cursor: pointer; font-family: inherit; border-width: 1px; border-style: solid; }
    .chip-botao:disabled { opacity: .4; cursor: not-allowed; }
    .dica { margin-top: 10px; }
    .erro { margin-top: 12px; }
    .acoes { display: flex; gap: 8px; margin-top: 18px; }
    .vazio { padding: 20px 0; text-align: center; }
    .vazio p { margin: 0 0 6px; }
  `]
})
export class SeletorEtiquetas {
  /** O vocabulário inteiro da empresa — o teto de 60 garante que cabe numa resposta só. */
  vocabulario = input<EtiquetaDto[]>([]);

  /** As que o contato JÁ tem. Chegam por requisição própria, e podem chegar depois de o modal
   *  abrir — daí o `effect` lá embaixo. */
  atuais = input<EtiquetaDto[]>([]);

  contatoNome = input('');
  salvando = input(false);
  erro = input('');

  /** O teto de etiquetas por negócio, do SERVIDOR (`TetosDaEmpresa.etiquetasPorNegocio`), passado
   *  pela tela (AUD-XX). Era um 8 copiado aqui. */
  maximo = input(0);

  /** Onde a etiqueta pode ficar (BUG-XX). Vazio = o seletor de sempre, sobre `atuais`. */
  alvos = input<AlvoEtiquetas[]>([]);
  /** O alvo que abre escolhido: o do lugar de onde a pessoa veio. */
  alvoInicial = input('');

  confirmado = output<number[]>();
  /** Com `alvos`: só os alvos que MUDARAM, cada um com a lista inteira dele. */
  confirmadoNosAlvos = output<EtiquetasNoAlvo[]>();
  cancelar = output<void>();

  alvo = signal('');
  /** O que a pessoa marcou em cada alvo por onde passou — trocar de alvo não perde a escolha. */
  private rascunhos = new Map<string, ReadonlySet<number>>();

  private alvoAtual = computed(() => this.alvos().find(a => a.chave === this.alvo()) ?? null);
  avisoDoAlvo = computed(() => this.alvoAtual()?.aviso ?? '');
  private atuaisDoAlvo = computed(() =>
    this.alvos().length > 0 ? (this.alvoAtual()?.atuais ?? []) : this.atuais());

  marcadas = signal<ReadonlySet<number>>(new Set());

  /** ⚠️ Guarda se a pessoa já mexeu. Sem isto, uma resposta lenta de `atuais` chegaria DEPOIS do
   *  primeiro clique e desfaria a escolha dela — o defeito clássico de semear estado com dado
   *  assíncrono. O `modal-fechamento` resolve o mesmo problema com `canalId() === null`. */
  private tocado = signal(false);

  textoSobre = textoSobre;

  cheio = computed(() => this.marcadas().size >= this.maximo());

  constructor() {
    // O alvo de partida, quando os alvos chegam. Uma vez só: depois, quem escolhe é a pessoa.
    effect(() => {
      const alvos = this.alvos();
      if (alvos.length === 0 || this.alvo() !== '') return;
      const inicial = this.alvoInicial();
      this.alvo.set(alvos.some(a => a.chave === inicial) ? inicial : alvos[0].chave);
    });

    effect(() => {
      const atuais = this.atuaisDoAlvo();
      if (this.tocado()) return;
      this.marcadas.set(new Set(atuais.map(e => e.id)));
    });
  }

  escolherAlvo(chave: string) {
    if (chave === this.alvo()) return;
    if (this.tocado()) this.rascunhos.set(this.alvo(), this.marcadas());
    const rascunho = this.rascunhos.get(chave);
    this.alvo.set(chave);
    this.tocado.set(rascunho !== undefined);
    if (rascunho) this.marcadas.set(rascunho);
    else {
      const atuais = this.alvos().find(a => a.chave === chave)?.atuais ?? [];
      this.marcadas.set(new Set(atuais.map(e => e.id)));
    }
  }

  alternar(id: number) {
    this.tocado.set(true);
    const copia = new Set(this.marcadas());
    if (copia.has(id)) copia.delete(id);
    else if (copia.size < this.maximo()) copia.add(id);
    this.marcadas.set(copia);
  }

  confirmar() {
    if (this.alvos().length === 0) {
      this.confirmado.emit([...this.marcadas()]);
      return;
    }

    if (this.tocado()) this.rascunhos.set(this.alvo(), this.marcadas());
    const mudancas: EtiquetasNoAlvo[] = [];
    for (const a of this.alvos()) {
      const rascunho = this.rascunhos.get(a.chave);
      if (!rascunho) continue;
      const antes = new Set(a.atuais.map(e => e.id));
      const igual = antes.size === rascunho.size && [...rascunho].every(id => antes.has(id));
      if (!igual) mudancas.push({ chave: a.chave, ids: [...rascunho] });
    }
    this.confirmadoNosAlvos.emit(mudancas);
  }

  aoTeclar(evento: KeyboardEvent) {
    if (evento.key === 'Escape') { evento.preventDefault(); this.cancelar.emit(); }
  }
}
