import { Component, computed, input, output } from '@angular/core';

/** Quantos registros por página em TODA tabela do painel.
 *
 *  ===================== POR QUE UM NÚMERO SÓ =====================
 *  Antes cada tela escolhia o seu: contatos vinha de 30 em 30, equipe e feriados carregavam
 *  tudo de uma vez. O usuário aprende o comportamento numa tela e ele muda na seguinte — e a
 *  tela que carrega tudo vira uma parede de centenas de linhas no dia em que a base cresce.
 *  ================================================================ */
export const POR_PAGINA = 20;

/** Recorta um array já carregado para a página pedida.
 *
 *  Para as listas em que a API devolve o conjunto inteiro (equipe, feriados, lembretes do
 *  contato). Onde a API pagina de verdade — contatos —, o recorte é do servidor e esta função
 *  não entra: paginar de novo no cliente mostraria 20 de 20 e esconderia o resto. */
export function fatiar<T>(itens: readonly T[], pagina: number, porPagina = POR_PAGINA): T[] {
  const inicio = (pagina - 1) * porPagina;
  return itens.slice(inicio, inicio + porPagina);
}

export function totalDePaginas(total: number, porPagina = POR_PAGINA): number {
  return Math.max(1, Math.ceil(total / porPagina));
}

/** Quantas linhas-fantasma faltam para a última página ter a mesma altura das cheias.
 *
 *  ===================== POR QUE ISTO IMPORTA =====================
 *  Sem o preenchimento, a última página (3 linhas em vez de 20) encolhe a tabela e o controle de
 *  paginação SOBE uns 400px. Quem estava clicando em "próxima" repetidamente erra o clique — o
 *  botão saiu de baixo do cursor no instante em que a página trocou.
 *  ================================================================ */
export function linhasFantasma(visiveis: number, porPagina = POR_PAGINA): number[] {
  // Só preenche quando há mais de uma página. Numa lista de 3 itens no total, esticar a tabela
  // para 20 linhas seria espaço morto sem motivo.
  return Array.from({ length: Math.max(0, porPagina - visiveis) }, (_, i) => i);
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
