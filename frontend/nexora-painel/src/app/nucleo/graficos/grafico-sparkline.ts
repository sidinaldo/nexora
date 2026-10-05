import { Component, computed, input } from '@angular/core';

/** ===================== UMA LINHA MINÚSCULA DENTRO DE UMA CÉLULA (EVO-1) =====================
 *
 *  Não é o `GraficoLinha` encolhido, e tentar reusá-lo foi a primeira ideia: ele tem 280px de
 *  altura, eixo, tooltip e média móvel. Numa célula de tabela nada disso cabe, e o tooltip
 *  competiria com o clique que seleciona a pessoa.
 *
 *  ⚠️ A ESCALA VEM DE FORA, e é o que faz este componente valer a pena numa TABELA. Se cada linha
 *  escalasse pelos próprios extremos, uma pessoa que oscilou entre 29% e 31% desenharia a mesma
 *  montanha de outra que foi de 10% a 60% — e as duas ficariam lado a lado, convidando a uma
 *  comparação que a imagem torna impossível. Quem calcula o par é a tela, sobre TODAS as pessoas.
 *
 *  ⚠️ BURACO NÃO SE INTERPOLA. Mês sem nada decidido chega `null`, e ligar os vizinhos por cima
 *  dele desenharia uma subida que ninguém mediu. A linha QUEBRA e vira dois segmentos — é por isso
 *  que `caminhos` devolve uma lista, e não um `d` só.
 *  ============================================================================================ */
@Component({
  selector: 'app-grafico-sparkline',
  template: `
    @if (temLinha()) {
      <svg [attr.viewBox]="'0 0 ' + W + ' ' + H" [attr.width]="W" [attr.height]="H"
           [attr.aria-label]="rotulo()" role="img" class="spark">
        @for (d of caminhos(); track $index) {
          <path [attr.d]="d" fill="none" [attr.stroke]="cor()" stroke-width="1.5"
                stroke-linecap="round" />
        }
        <!-- O último ponto marcado: num traço de 78px, é o que diz onde a pessoa está AGORA. -->
        @if (fim(); as p) {
          <circle [attr.cx]="p.x" [attr.cy]="p.y" r="2.2" [attr.fill]="cor()" />
        }
      </svg>
    } @else {
      <span class="sem-linha" aria-hidden="true">—</span>
    }
  `,
  styles: `
    :host { display: inline-flex; align-items: center; }
    .spark { display: block; overflow: visible; }
    .sem-linha { color: var(--texto-fraco); font-size: 12px; }
  `
})
export class GraficoSparkline {
  /** Um valor por mês, na ordem. `null` = mês sem nada decidido. */
  pontos = input<(number | null)[]>([]);

  /** Os extremos da escala, calculados pela TELA sobre todas as linhas da tabela. Iguais entre si
   *  (série constante) viram uma reta no meio — e é a leitura certa: nada mudou. */
  minimo = input(0);
  maximo = input(1);

  cor = input('var(--texto-fraco)');
  rotulo = input('');

  readonly W = 78;
  readonly H = 24;
  readonly pad = 3;

  private validos = computed(() =>
    this.pontos()
      .map((v, i) => ({ v, i }))
      .filter((p): p is { v: number; i: number } => p.v !== null));

  /** Dois pontos no mínimo: um ponto só não tem direção, e desenhá-lo no alto (que é onde a escala
   *  o põe) insinuaria um pico que não se mediu contra nada. Mesma razão do `GraficoLinha`. */
  temLinha = computed(() => this.validos().length >= 2);

  private x(i: number): number {
    const n = this.pontos().length;
    return n <= 1 ? this.W / 2 : this.pad + (i / (n - 1)) * (this.W - 2 * this.pad);
  }

  private y(v: number): number {
    const faixa = this.maximo() - this.minimo();
    // Faixa zero = todos iguais: desenha no meio em vez de dividir por zero.
    const fracao = faixa <= 0 ? 0.5 : (v - this.minimo()) / faixa;

    return this.H - this.pad - fracao * (this.H - 2 * this.pad);
  }

  /** Um `d` por trecho contínuo. Quebra onde houve mês vazio. */
  caminhos = computed(() => {
    const ds: string[] = [];
    let atual: string[] = [];
    let anterior = -2;

    for (const p of this.validos()) {
      if (p.i !== anterior + 1 && atual.length > 0) {
        if (atual.length > 1) ds.push(atual.join(' '));
        atual = [];
      }

      atual.push(`${atual.length ? 'L' : 'M'}${this.x(p.i)},${this.y(p.v)}`);
      anterior = p.i;
    }

    if (atual.length > 1) ds.push(atual.join(' '));

    return ds;
  });

  fim = computed(() => {
    const ultimo = this.validos().at(-1);

    return ultimo && this.temLinha() ? { x: this.x(ultimo.i), y: this.y(ultimo.v) } : null;
  });
}
