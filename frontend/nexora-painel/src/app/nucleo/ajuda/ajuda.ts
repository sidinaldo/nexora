import { Component, input, signal } from '@angular/core';

/** O ⓘ QUE EXPLICA UM NÚMERO SEM OCUPAR A TELA.
 *
 *  ===================== POR QUE NÃO É UM POPOVER =====================
 *  `paginas.celular.spec.ts` varre toda tela e REPROVA `position: fixed` ou `sticky` fora de
 *  `.overlay` e `.pilha`. Então o balão é `absolute` dentro de um pai `relative` — exatamente a
 *  geometria que `.gb-tip` e `.gl-tip`, os tooltips dos gráficos, já usam há tempo.
 *
 *  A consequência é real e aceita: o balão é recortado por um ancestral com `overflow: hidden`. Em
 *  troca, ele acompanha a rolagem da página sem uma linha de JavaScript — que é o que um popover
 *  posicionado à mão sempre erra primeiro.
 *  ====================================================================
 *
 *  ⚠️ ABRE POR CLIQUE, E NÃO SÓ POR HOVER. Em celular não existe hover: um ⓘ de mouse esconde a
 *  explicação justamente de quem está na tela pequena, que é onde o texto não cabia. O hover entra
 *  por cima, como atalho de quem tem mouse — nunca como a única porta.
 *
 *  Substitui parágrafos de rodapé que explicavam a conta de um relatório. O texto não some do
 *  produto; ele deixa de ocupar quatro linhas abaixo de cada cartão. */
@Component({
  selector: 'app-ajuda',
  template: `
    <span class="ajuda-raiz">
      <button type="button" class="ajuda-botao"
              [attr.aria-expanded]="aberto()"
              [attr.aria-label]="aberto() ? 'Fechar a explicação' : 'O que este número quer dizer'"
              (click)="aberto.set(!aberto())"
              (pointerenter)="aberto.set(true)"
              (pointerleave)="aoSair($event)">ⓘ</button>

      @if (aberto()) {
        <span class="ajuda-balao" role="note">{{ texto() }}</span>
      }
    </span>
  `,
  styles: [`
    .ajuda-raiz { position: relative; display: inline-flex; vertical-align: middle; }

    .ajuda-botao {
      border: 0; background: none; padding: 0 2px; cursor: help;
      font-size: 13px; line-height: 1; color: var(--texto-fraco);
    }
    .ajuda-botao:hover { color: var(--verde-2); }
    .ajuda-botao[aria-expanded="true"] { color: var(--verde); }

    /* "absolute", nunca "fixed" — ver o comentário da classe. O balão nasce à esquerda do ícone
       porque os ⓘ desta tela vivem à direita de títulos, e abrindo para a direita ele sairia da
       largura do cartão. */
    .ajuda-balao {
      position: absolute; top: calc(100% + 6px); right: 0; z-index: 1;
      width: max-content; max-width: 280px;
      background: var(--verde); color: var(--branco);
      border-radius: 8px; padding: 8px 10px;
      font-size: 12px; font-weight: 400; line-height: 1.45; text-transform: none;
      letter-spacing: 0; text-align: left;
      box-shadow: var(--sombra);
      pointer-events: none;
    }
  `]
})
export class Ajuda {
  texto = input.required<string>();

  aberto = signal(false);

  /** ⚠️ O TOQUE NÃO FECHA NO `pointerleave`. Em celular, o toque dispara `enter` e `leave` quase
   *  juntos — o balão abriria e sumiria no mesmo gesto. É a mesma regra que `grafico-linha` e
   *  `grafico-barras` já aplicam no `sair()` deles. */
  aoSair(e: PointerEvent) {
    if (e.pointerType === 'mouse') this.aberto.set(false);
  }
}
