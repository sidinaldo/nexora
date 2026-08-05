import { Component, computed, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';

export interface PontoSerie { data: string; valor: number; }

interface Hover { x: number; y: number; pct: number; data: string; valor: number; }

/** Gráfico de linha/área em SVG inline, sem biblioteca. Recebe uma série (data + valor) e
 *  desenha área, linha, média móvel opcional e tooltip no hover.
 *
 *  PORTADO do `grafico-linha` do Recupera (nível B do inventário). O desenho é o mesmo; o que
 *  mudou:
 *   - os tokens de cor passaram para a paleta do Nexora (verde, não âmbar);
 *   - o texto de vazio deixou de ser "Sem recuperação no período" — vocabulário de cobrança —
 *     e virou parâmetro;
 *   - o valor não é mais formatado como moeda à força: `formato` permite série de CONTAGEM
 *     (leads por dia) além de série de dinheiro.
 *
 *  ⚠️ AINDA NÃO ESTÁ LIGADO EM NENHUMA TELA. Não existe endpoint que devolva série temporal —
 *  ver a seção de API faltante em docs/BLOCO-9.md. O componente fica pronto para o dia em que
 *  esse endpoint existir; alimentá-lo hoje exigiria agregar em memória sobre uma lista
 *  paginada, que é justamente o que o projeto proíbe. */
@Component({
  selector: 'app-grafico-linha',
  imports: [DatePipe],
  templateUrl: './grafico-linha.html',
  styleUrl: './grafico-linha.css'
})
export class GraficoLinha {
  serie = input<PontoSerie[]>([]);
  /** Janela da média móvel; 0 desliga. */
  mediaMovel = input(7);
  formato = input<'moeda' | 'numero'>('moeda');
  rotuloVazio = input('Sem dados no período.');

  readonly W = 1000;
  readonly H = 280;
  readonly pad = 10;

  hover = signal<Hover | null>(null);

  private max = computed(() => Math.max(1, ...this.serie().map(p => p.valor)));

  private px(i: number): number {
    const n = this.serie().length;
    return n <= 1 ? this.W / 2 : this.pad + (i / (n - 1)) * (this.W - 2 * this.pad);
  }

  private py(v: number): number {
    return this.H - this.pad - (v / this.max()) * (this.H - 2 * this.pad);
  }

  linhaPath = computed(() =>
    this.serie().map((p, i) => `${i ? 'L' : 'M'}${this.px(i)},${this.py(p.valor)}`).join(' '));

  areaPath = computed(() => {
    const s = this.serie();
    if (s.length === 0) return '';
    const base = this.H - this.pad;
    return `${this.linhaPath()} L${this.px(s.length - 1)},${base} L${this.px(0)},${base} Z`;
  });

  mmPath = computed(() => {
    const w = this.mediaMovel();
    const s = this.serie();
    if (w < 2 || s.length < w) return '';
    const pts: string[] = [];
    for (let i = 0; i < s.length; i++) {
      const janela = s.slice(Math.max(0, i - w + 1), i + 1);
      const media = janela.reduce((a, p) => a + p.valor, 0) / janela.length;
      pts.push(`${i ? 'L' : 'M'}${this.px(i)},${this.py(media)}`);
    }
    return pts.join(' ');
  });

  temDados = computed(() => this.serie().some(p => p.valor > 0));

  rotuloValor(v: number): string {
    return this.formato() === 'moeda'
      ? v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' })
      : v.toLocaleString('pt-BR');
  }

  mover(ev: MouseEvent) {
    const el = ev.currentTarget as HTMLElement;
    const rect = el.getBoundingClientRect();
    const n = this.serie().length;
    if (n === 0 || rect.width === 0) return;

    const f = Math.min(1, Math.max(0, (ev.clientX - rect.left) / rect.width));
    const i = Math.round(f * (n - 1));
    const p = this.serie()[i];
    this.hover.set({
      x: this.px(i), y: this.py(p.valor),
      pct: n <= 1 ? 50 : (i / (n - 1)) * 100,
      data: p.data, valor: p.valor
    });
  }
}
