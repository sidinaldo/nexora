import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Ajuda } from './ajuda';

/** O ⓘ DOS RELATÓRIOS (REL-1).
 *
 *  ===================== O QUE ESTES TESTES PROTEGEM =====================
 *  Ele substituiu parágrafos de rodapé que explicavam a conta de cada relatório. O texto não podia
 *  sumir do produto — só sair da frente.
 *
 *  Duas coisas não podem quebrar:
 *
 *    1. abre por CLIQUE. Em celular não existe hover, e um ⓘ de mouse esconderia a explicação
 *       justamente de quem está na tela pequena — que é onde o texto não cabia, e a razão de ele
 *       ter virado ícone;
 *    2. o toque não fecha no `pointerleave`. No celular o toque dispara `enter` e `leave` quase
 *       juntos: o balão abriria e sumiria no mesmo gesto. É a mesma regra que os gráficos já
 *       aplicam.
 *  ====================================================================== */
describe('ⓘ de ajuda', () => {
  let fixture: ComponentFixture<Ajuda>;
  let c: Ajuda;

  function montar() {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    fixture = TestBed.createComponent(Ajuda);
    c = fixture.componentInstance;
    fixture.componentRef.setInput('texto', 'Conversão = vendas ÷ (vendas + perdidos).');
    fixture.detectChanges();
  }

  const botao = () => (fixture.nativeElement as HTMLElement)
    .querySelector<HTMLButtonElement>('.ajuda-botao')!;
  const balao = () => (fixture.nativeElement as HTMLElement).querySelector('.ajuda-balao');

  it('nasce fechado', () => {
    montar();
    expect(balao()).toBeNull();
    expect(botao().getAttribute('aria-expanded')).toBe('false');
  });

  /** ⚠️ O TESTE QUE IMPORTA. Sem ele, um ⓘ só de hover passa em toda a suíte e deixa o celular
   *  sem a explicação — e ninguém descobre até alguém abrir o relatório no telefone. */
  it('O ⓘ ABRE POR CLIQUE', () => {
    montar();

    botao().click();
    fixture.detectChanges();

    expect(balao()!.textContent).toContain('Conversão = vendas');
    expect(botao().getAttribute('aria-expanded')).toBe('true');

    // E fecha no segundo clique — senão, no celular, ele só some recarregando a página.
    botao().click();
    fixture.detectChanges();
    expect(balao()).toBeNull();
  });

  it('o mouse abre ao entrar e fecha ao sair', () => {
    montar();

    botao().dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' }));
    fixture.detectChanges();
    expect(balao()).not.toBeNull();

    botao().dispatchEvent(new PointerEvent('pointerleave', { pointerType: 'mouse' }));
    fixture.detectChanges();
    expect(balao()).toBeNull();
  });

  /** ===================== NEM `fixed`, NEM `sticky` =====================
   *  `paginas.celular.spec.ts` varre as telas atras de posicionamento preso e reprova — mas ele
   *  **nao cobre este componente**: a varredura so ve o que esta no DOM, e o balao nasce fechado.
   *  Verifiquei sabotando: trocar `absolute` por `fixed` passa pela suite inteira.
   *
   *  ⚠️ ENTAO O GUARDA E ESTE AQUI. Com `fixed`, o balao se descola do icone na rolagem e vai
   *  parar no meio da tela, longe do numero que explica — e em celular, onde a pagina rola o tempo
   *  todo, ele vira um retangulo flutuando sobre o conteudo.
   *  ====================================================================== */
  it('o balão é POSICIONADO NO FLUXO, nunca preso à janela', () => {
    montar();
    botao().click();
    fixture.detectChanges();

    const posicao = getComputedStyle(balao()!).position;

    expect(posicao).toBe('absolute');
    expect(['fixed', 'sticky']).not.toContain(posicao);
  });

  /** O toque dispara `enter` e `leave` quase juntos. Se o `leave` fechasse, o balão apareceria e
   *  sumiria no mesmo toque — que é como um tooltip de mouse se comporta num celular, e por isso
   *  nenhum deles funciona lá. */
  it('o TOQUE não fecha ao sair', () => {
    montar();

    botao().dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'touch' }));
    fixture.detectChanges();
    expect(balao()).not.toBeNull();

    botao().dispatchEvent(new PointerEvent('pointerleave', { pointerType: 'touch' }));
    fixture.detectChanges();
    expect(balao()).not.toBeNull();
  });
});
