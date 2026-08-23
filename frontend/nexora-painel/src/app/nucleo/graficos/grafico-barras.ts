import { Component, computed, input, signal } from '@angular/core';

export interface BarraGrafico {
  /** O que vai no eixo. Já formatado — o componente não sabe se é data, nome ou origem. */
  rotulo: string;
  valor: number;
  /** Parcela do `valor` que recebe o tom escuro. Usada para mostrar "quanto do faturamento já
   *  está concluído" dentro da mesma barra, sem virar duas séries que ninguém compara. */
  destaque?: number;
}

interface Hover { indice: number; x: number; }

/** Barras verticais em SVG inline, sem biblioteca.
 *
 *  Irmão do `grafico-linha`: mesmas medidas, mesmo `viewBox`, mesma paleta. Série temporal pede
 *  linha (a tendência importa); comparação entre categorias pede barra — "qual origem vende mais"
 *  não tem tendência nenhuma, e ligar os pontos sugeriria uma que não existe.
 *
 *  DEGRADÊ VERDE, só tokens do design system. A barra em destaque usa o verde escuro da marca; o
 *  resto usa o claro. Nenhuma cor nova entra aqui. */
@Component({
  selector: 'app-grafico-barras',
  templateUrl: './grafico-barras.html',
  styleUrl: './grafico-barras.css'
})
export class GraficoBarras {
  barras = input<BarraGrafico[]>([]);
  formato = input<'moeda' | 'numero'>('moeda');
  rotuloVazio = input('Sem dados no período.');
  /** Legenda da parte escura da barra. Vazio = sem destaque. */
  rotuloDestaque = input('');

  readonly W = 1000;
  readonly H = 280;
  readonly pad = 10;
  /** Espaço para os rótulos do eixo. Fora do `pad` porque só o rodapé precisa dele. */
  readonly eixo = 26;

  hover = signal<Hover | null>(null);

  /** O topo da escala. `Math.max(1, ...)` evita divisão por zero na série toda-zero — e mantém as
   *  barras rentes ao chão em vez de fazer o zero preencher a altura inteira. */
  private max = computed(() => Math.max(1, ...this.barras().map(b => b.valor)));

  /** Largura de uma fatia do eixo, incluindo o vão. */
  private fatia = computed(() => {
    const n = this.barras().length;
    return n === 0 ? 0 : (this.W - 2 * this.pad) / n;
  });

  /** O vão fica em 22% da fatia, com teto de 24px no `viewBox`: sem teto, três barras viram três
   *  tarjas grossas separadas por corredores, e a comparação entre elas fica mais difícil, não
   *  mais fácil. */
  larguraBarra = computed(() => {
    const f = this.fatia();
    return Math.max(2, f - Math.min(24, f * 0.22));
  });

  x(i: number): number {
    return this.pad + i * this.fatia() + (this.fatia() - this.larguraBarra()) / 2;
  }

  private altura(v: number): number {
    const util = this.H - 2 * this.pad - this.eixo;
    return Math.max(0, (v / this.max()) * util);
  }

  y(v: number): number {
    return this.H - this.pad - this.eixo - this.altura(v);
  }

  alturaDe(v: number): number { return this.altura(v); }

  /** A parte escura nunca ultrapassa a barra: `destaque` maior que `valor` seria dado
   *  inconsistente do servidor, e desenhar por cima esconderia o problema. */
  alturaDestaque(b: BarraGrafico): number {
    return this.altura(Math.min(b.destaque ?? 0, b.valor));
  }

  yDestaque(b: BarraGrafico): number {
    return this.H - this.pad - this.eixo - this.alturaDestaque(b);
  }

  /** Centro da fatia, para o rótulo do eixo. */
  centro(i: number): number {
    return this.pad + i * this.fatia() + this.fatia() / 2;
  }

  temDados = computed(() => this.barras().some(b => b.valor > 0));

  /** Com muitas barras os rótulos colidem; mostrar um a cada N é melhor que sobrepor texto.
   *  30 barras num `viewBox` de 1000 dá ~33px por rótulo, que é o limite do legível. */
  passoRotulo = computed(() => Math.ceil(this.barras().length / 30));

  mostrarRotulo(i: number): boolean {
    return i % this.passoRotulo() === 0;
  }

  rotuloValor(v: number): string {
    return this.formato() === 'moeda'
      ? v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' })
      : v.toLocaleString('pt-BR');
  }

  mover(ev: PointerEvent) {
    const el = ev.currentTarget as HTMLElement;
    const rect = el.getBoundingClientRect();
    const n = this.barras().length;
    if (n === 0 || rect.width === 0) return;

    const f = Math.min(0.999, Math.max(0, (ev.clientX - rect.left) / rect.width));
    const i = Math.min(n - 1, Math.floor(f * n));
    this.hover.set({ indice: i, x: (i + 0.5) * (100 / n) });
  }

  /** ⚠️ EM TOQUE, `pointerleave` DISPARA AO LEVANTAR O DEDO. Esconder ali faria o valor piscar
   *  e sumir dentro do mesmo gesto — o dedo mal encostou e a etiqueta já foi. Com mouse o
   *  comportamento continua o de sempre: saiu do gráfico, some. */
  sair(ev?: PointerEvent) {
    if (ev && ev.pointerType !== 'mouse') return;
    this.hover.set(null);
  }

  barraSobHover = computed(() => {
    const h = this.hover();
    return h === null ? null : this.barras()[h.indice] ?? null;
  });
}
