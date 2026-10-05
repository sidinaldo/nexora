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
 *  Ligado em DUAS telas: o dashboard (`/dashboard`, com seletor de métrica) e os relatórios
 *  (`/relatorios`, faturamento por período). O aviso que havia aqui — "ainda não está ligado em
 *  nenhuma tela, não existe endpoint de série temporal" — ficou velho quando
 *  `GET /api/dashboard/serie` nasceu. */
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

  /** O texto de "um período só". É PARÂMETRO porque quem sabe o nome do controle é a tela: o
   *  dashboard e os relatórios têm seletores de agrupamento com rótulos próprios, e o gráfico não
   *  deve inventar o vocabulário de nenhum dos dois. */
  rotuloUmPeriodo = input('Um período só — agrupe por dia para ver a evolução.');

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

  /** ===================== UM PONTO NÃO DESENHA LINHA =====================
   *
   *  Achado na tela: com "Agrupar por: Mês" num período de um mês só, a série vem com UM ponto — e
   *  o gráfico ficava um retângulo em branco. Nem desenho, nem explicação:
   *
   *    · `linhaPath` sai só com um `M`, que é um "mover até" sem segmento nenhum;
   *    · `areaPath` vira um polígono de largura zero;
   *    · e a mensagem de vazio não aparecia, porque `temDados()` pergunta por `valor > 0` e o
   *      valor existe.
   *
   *  ⚠️ E NÃO ADIANTA DESENHAR UM PONTINHO. Com um só, `max` é o próprio valor e `py` devolve o
   *  topo: a bolinha ficaria SEMPRE no alto, sugerindo um pico que não se mediu contra nada. Um
   *  gráfico de linha responde "está subindo?", e com um período a resposta não existe — dizer
   *  isso é mais honesto que desenhar algo que insinua uma tendência.
   *  ====================================================================== */
  temLinha = computed(() => this.temDados() && this.serie().length >= 2);

  umPeriodoSo = computed(() => this.temDados() && this.serie().length < 2);

  rotuloValor(v: number): string {
    return this.formato() === 'moeda'
      ? v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' })
      : v.toLocaleString('pt-BR');
  }

  mover(ev: PointerEvent) {
    const el = ev.currentTarget as HTMLElement;
    const rect = el.getBoundingClientRect();
    const n = this.serie().length;

    // ⚠️ SEM LINHA DESENHADA, SEM TOOLTIP. Senão o dedo faz aparecer uma guia e um valor sobre uma
    // área em branco — um número que não corresponde a nada visível na tela.
    if (!this.temLinha() || n === 0 || rect.width === 0) return;

    const f = Math.min(1, Math.max(0, (ev.clientX - rect.left) / rect.width));
    const i = Math.round(f * (n - 1));
    const p = this.serie()[i];
    this.hover.set({
      x: this.px(i), y: this.py(p.valor),
      pct: n <= 1 ? 50 : (i / (n - 1)) * 100,
      data: p.data, valor: p.valor
    });
  }

  /** ⚠️ EM TOQUE, `pointerleave` DISPARA AO LEVANTAR O DEDO. Esconder ali faria o valor piscar e
   *  sumir dentro do mesmo gesto. Com mouse continua o de sempre. Era `hover.set(null)` escrito
   *  no template; virou método porque agora tem uma regra dentro. */
  sair(ev?: PointerEvent) {
    if (ev && ev.pointerType !== 'mouse') return;
    this.hover.set(null);
  }
}
