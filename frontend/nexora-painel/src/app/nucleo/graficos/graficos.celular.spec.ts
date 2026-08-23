import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { GraficoBarras } from './grafico-barras';
import { GraficoLinha } from './grafico-linha';

/** ===================== O GRÁFICO PRECISA RESPONDER AO DEDO (MOB-2) =====================
 *  Os dois gráficos mostravam o VALOR EXATO numa etiqueta presa a `mousemove`. No celular não
 *  existe passar o mouse: a etiqueta nunca aparecia, e sobrava a forma sem número nenhum — quem
 *  abria o dashboard no aparelho via a tendência e não conseguia ler quanto.
 *
 *  `PointerEvent` cobre mouse, dedo e caneta com o mesmo código. `pointerdown` entra junto porque
 *  em toque o `pointermove` só dispara com o dedo JÁ apoiado: sem ele, um toque simples não
 *  mostraria nada.
 *  ======================================================================================= */
describe('gráficos respondem a toque', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  });
  afterEach(() => TestBed.resetTestingModule());

  /** Um toque de VERDADE: `pointerType: 'touch'` é o que distingue dedo de mouse, e é sobre ele
   *  que a regra de esconder decide. */
  function tocar(alvo: Element, tipo: string, x: number) {
    alvo.dispatchEvent(new PointerEvent(tipo, {
      bubbles: true, clientX: x, clientY: 10, pointerType: 'touch', isPrimary: true
    }));
  }

  it('BARRAS: um toque mostra o valor', async () => {
    const f = TestBed.createComponent(GraficoBarras);
    f.componentRef.setInput('barras', [
      { rotulo: 'jan', valor: 1000, destaque: 0 },
      { rotulo: 'fev', valor: 2500, destaque: 0 }
    ]);
    document.body.appendChild(f.nativeElement);
    f.detectChanges();

    const raiz = f.nativeElement as HTMLElement;
    expect(raiz.querySelector('.gb-tip')).withContext('a etiqueta já nasceu na tela').toBeNull();

    const wrap = raiz.querySelector('.gb-wrap')!;
    const r = wrap.getBoundingClientRect();
    tocar(wrap, 'pointerdown', r.left + r.width * 0.75);
    await f.whenStable();
    f.detectChanges();

    expect(raiz.querySelector('.gb-tip'))
      .withContext('o toque não trouxe o valor — no celular o gráfico fica sem número')
      .not.toBeNull();

    // ⚠️ Levantar o dedo NÃO pode esconder: `pointerleave` dispara no toque final, e esconder ali
    // faria o valor piscar e sumir dentro do mesmo gesto.
    tocar(wrap, 'pointerleave', r.left + r.width * 0.75);
    await f.whenStable();
    f.detectChanges();
    expect(raiz.querySelector('.gb-tip'))
      .withContext('o valor sumiu ao levantar o dedo — o gesto inteiro não mostra nada')
      .not.toBeNull();

    f.nativeElement.remove();
  });

  it('LINHA: um toque mostra o valor', async () => {
    const f = TestBed.createComponent(GraficoLinha);
    f.componentRef.setInput('serie', [
      { data: '2026-08-01', valor: 100 },
      { data: '2026-08-02', valor: 300 }
    ]);
    document.body.appendChild(f.nativeElement);
    f.detectChanges();

    const raiz = f.nativeElement as HTMLElement;
    const wrap = raiz.querySelector('.gl-wrap')!;
    const r = wrap.getBoundingClientRect();
    tocar(wrap, 'pointerdown', r.left + r.width * 0.75);
    await f.whenStable();
    f.detectChanges();

    expect(raiz.querySelector('.gl-tip'))
      .withContext('o toque não trouxe o valor — no celular o gráfico fica sem número')
      .not.toBeNull();

    f.nativeElement.remove();
  });
});
