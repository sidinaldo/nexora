import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { GraficoLinha, PontoSerie } from './grafico-linha';

/** ===================== O GRÁFICO DE LINHA, NOS ESTADOS QUE NÃO SÃO LINHA =====================
 *
 *  O componente tinha só o teste de toque (`graficos.celular.spec.ts`). Este arquivo nasceu de um
 *  defeito achado NA TELA: agrupando por mês num período de um mês, o gráfico ficava um retângulo
 *  em branco — nem desenho, nem explicação.
 *
 *  ⚠️ UM PONTO NÃO É "SEM DADOS", e é por isso que o estado passou despercebido: `temDados()`
 *  pergunta por `valor > 0`, e o valor existia. O que não existia era segmento para desenhar.
 *  ============================================================================================ */
describe('gráfico de linha — quando não há linha para desenhar', () => {
  let fixture: ComponentFixture<GraficoLinha>;
  let raiz: HTMLElement;

  function montar(serie: PontoSerie[], mediaMovel = 0) {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()]
    });

    fixture = TestBed.createComponent(GraficoLinha);
    fixture.componentRef.setInput('serie', serie);
    fixture.componentRef.setInput('mediaMovel', mediaMovel);
    fixture.detectChanges();

    raiz = fixture.nativeElement as HTMLElement;
  }

  afterEach(() => TestBed.resetTestingModule());

  function traco(): string {
    return raiz.querySelector('.gl-linha')?.getAttribute('d') ?? '';
  }

  it('COM DOIS PONTOS OU MAIS, DESENHA', () => {
    montar([
      { data: '2026-09-01', valor: 1000 },
      { data: '2026-10-01', valor: 1400 }
    ]);

    expect(raiz.querySelector('.gl-linha')).not.toBeNull();
    expect(traco()).withContext('um M e um L: o segmento existe').toContain('L');
    expect(raiz.querySelector('.gl-vazio')).toBeNull();
  });

  /** ===================== O DEFEITO QUE ESTE ARQUIVO EXISTE PARA IMPEDIR =====================
   *  ⚠️ O PAR É O PONTO. Afirmar só que "não desenha" passaria numa versão que não desenhasse
   *  NUNCA; afirmar só a mensagem passaria numa versão que a mostrasse por cima de uma linha de
   *  verdade. Os dois juntos prendem o estado.
   *  ========================================================================================== */
  it('COM UM PONTO SÓ, NÃO DESENHA E DIZ POR QUÊ', () => {
    montar([{ data: '2026-10-01', valor: 14458.32 }]);

    expect(raiz.querySelector('.gl-linha'))
      .withContext('um `M` solto não é linha — não se desenha nada').toBeNull();
    expect(raiz.querySelector('.gl-area')).toBeNull();

    expect(raiz.querySelector('.gl-vazio')?.textContent)
      .toContain('Um período só');
  });

  /** Série vazia continua com a frase de sempre — são dois estados mudos DIFERENTES, e trocá-los
   *  diria "agrupe por dia" para quem simplesmente não vendeu nada. */
  it('SEM NENHUM PONTO, A FRASE É A DE SEMPRE', () => {
    montar([]);

    expect(raiz.querySelector('.gl-vazio')?.textContent).toContain('Sem dados no período');
  });

  /** Vários pontos, todos zero: é "sem dados", não "um período só". */
  it('COM TUDO ZERADO, TAMBÉM É SEM DADOS', () => {
    montar([
      { data: '2026-10-01', valor: 0 },
      { data: '2026-10-02', valor: 0 }
    ]);

    expect(raiz.querySelector('.gl-vazio')?.textContent).toContain('Sem dados no período');
    expect(raiz.querySelector('.gl-linha')).toBeNull();
  });

  /** ⚠️ SEM LINHA, SEM TOOLTIP. Senão o dedo faz aparecer uma guia e um valor sobre área em
   *  branco — um número que não corresponde a nada visível. */
  it('COM UM PONTO SÓ, O TOQUE NÃO MOSTRA VALOR', () => {
    montar([{ data: '2026-10-01', valor: 14458.32 }]);

    const caixa = raiz.querySelector<HTMLElement>('.gl-wrap')!;
    caixa.dispatchEvent(new PointerEvent('pointerdown', { clientX: 50, bubbles: true }));
    fixture.detectChanges();

    expect(raiz.querySelector('.gl-tip')).toBeNull();
    expect(raiz.querySelector('.gl-guia')).toBeNull();
  });

  /** A média móvel já exigia a janela cheia, e segue exigindo — o estado novo não a liberou. */
  it('A MÉDIA MÓVEL CONTINUA PEDINDO A JANELA INTEIRA', () => {
    montar([
      { data: '2026-10-01', valor: 10 },
      { data: '2026-10-02', valor: 20 }
    ], 7);

    expect(raiz.querySelector('.gl-mm')).withContext('dois pontos, janela de sete').toBeNull();
  });
});
