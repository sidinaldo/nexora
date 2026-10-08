import { Component, computed, input, output } from '@angular/core';

/** Quantos registros por página em TODA tabela do painel.
 *
 *  ===================== POR QUE UM NÚMERO SÓ =====================
 *  Antes cada tela escolhia o seu: contatos vinha de 30 em 30, equipe e feriados carregavam
 *  tudo de uma vez. O usuário aprende o comportamento numa tela e ele muda na seguinte — e a
 *  tela que carrega tudo vira uma parede de centenas de linhas no dia em que a base cresce.
 *  ================================================================ */
export const POR_PAGINA = 20;

// ⚠️ `fatiar` E `totalDePaginas` SAÍRAM (AUD-XX, #21). Toda tabela do painel recebe a página
// pronta do servidor — `{ itens, totalCount, pagina, tamanhoPagina, totalPaginas }` — e a tela
// não recorta lista nem divide total. Equipe, feriados, canais e formulários vinham inteiros e
// eram recortados aqui; agora paginam no banco.

/** A altura mínima do container da tabela, em pixels.
 *
 *  ===================== O QUE MUDOU E POR QUÊ =====================
 *  O DES-1 pediu que "a tabela não mudasse de altura entre páginas" e a resposta foi preencher a
 *  última página com linhas VAZIAS até 20. Funcionava e estava errado: a última página de 350
 *  contatos mostrava 10 registros e 10 faixas em branco com borda, indistinguíveis de linhas com
 *  dado que não carregou. O usuário não tem como saber que aquilo não é registro.
 *
 *  A estabilidade é do CONTAINER, não das linhas. O elemento que envolve a tabela reserva a
 *  altura de uma página cheia; a tabela renderiza só o que existe. Se sobrar espaço abaixo da
 *  última linha, ele é do container — sem borda, sem listra, sem parecer registro.
 *
 *  44px por linha (a altura de `.tabela td` com o padding de 13px) mais ~46px de cabeçalho.
 *  =================================================================== */
export function alturaMinimaDaTabela(porPagina = POR_PAGINA): number {
  return porPagina * 44 + 46;
}

/** O CONTROLE DE PAGINAÇÃO, um só para todas as tabelas.
 *
 *  Mostra onde a pessoa está e quanto existe — "Página 2 de 14 · 276 contatos". Sem o total, o
 *  usuário não sabe se vale a pena procurar navegando ou se é melhor filtrar.
 *
 *  Primeira e última existem porque "ir para o fim" com 14 páginas é 13 cliques. */
@Component({
  selector: 'app-paginacao',
  template: `
    @if (totalPaginas() > 1) {
      <nav class="paginacao" role="navigation" aria-label="Paginação">
        <span class="contagem fraco mono">
          Página {{ pagina() }} de {{ totalPaginas() }}
          @if (total() > 0) { <span class="separa">·</span> {{ total() }} {{ rotulo() }} }
        </span>

        <span class="espaco"></span>

        <div class="botoes">
          <button type="button" class="btn btn-neutro btn-pequeno" title="Primeira página"
                  [disabled]="pagina() <= 1" (click)="ir(1)">«</button>
          <button type="button" class="btn btn-neutro btn-pequeno"
                  [disabled]="pagina() <= 1" (click)="ir(pagina() - 1)">Anterior</button>
          <button type="button" class="btn btn-neutro btn-pequeno"
                  [disabled]="pagina() >= totalPaginas()" (click)="ir(pagina() + 1)">Próxima</button>
          <button type="button" class="btn btn-neutro btn-pequeno" title="Última página"
                  [disabled]="pagina() >= totalPaginas()" (click)="ir(totalPaginas())">»</button>
        </div>
      </nav>
    }
  `,
  styles: `
    .paginacao {
      display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
      padding: 11px 20px; border-top: 1px solid var(--linha);
    }
    .contagem { font-size: 12px; }
    .separa { padding: 0 2px; }
    .botoes { display: flex; gap: 6px; }

    /* No celular o controle empilha e os botões ocupam a linha inteira: alvo de toque grande é
       mais importante que compactação numa barra que só tem quatro ações. */
    @media (max-width: 560px) {
      .paginacao { flex-direction: column; align-items: stretch; gap: 8px; }
      .botoes { justify-content: space-between; }
      .botoes .btn { flex: 1; justify-content: center; }
    }
  `
})
export class Paginacao {
  pagina = input.required<number>();
  totalPaginas = input.required<number>();

  /** Quantos registros existem no TOTAL (não na página). Zero esconde a contagem. */
  total = input(0);

  /** O substantivo, no plural: "contatos", "feriados", "pessoas". */
  rotulo = input('registros');

  irPara = output<number>();

  protected ir(p: number) {
    if (p < 1 || p > this.totalPaginas() || p === this.pagina()) return;
    this.irPara.emit(p);
  }
}

/** Rola até o topo da TABELA, não da janela.
 *
 *  Trocar de página com a tabela no meio da tela deixaria a pessoa olhando para a linha 12 da
 *  página nova. E rolar a janela inteira até o topo seria pior: ela perderia o cabeçalho da
 *  tela e o filtro que acabou de aplicar. */
export function rolarParaTopoDaTabela(alvo: HTMLElement | undefined | null) {
  if (!alvo) return;
  const suave = !window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  alvo.scrollIntoView({ behavior: suave ? 'smooth' : 'auto', block: 'start' });
}
