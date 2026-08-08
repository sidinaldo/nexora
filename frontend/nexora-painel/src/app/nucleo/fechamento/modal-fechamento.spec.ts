import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ModalFechamento, OpcaoCanal, ResultadoFechamento } from './modal-fechamento';

/** O MODAL DE FECHAMENTO — e o campo de campanha do NEG-3.
 *
 *  ===================== O QUE ESTES TESTES PROTEGEM =====================
 *  O canal detectado chega DEPOIS que o modal abriu (é uma requisição), então o pré-preenchimento
 *  não pode acontecer na construção. Mas ele também não pode acontecer sempre: detectado que
 *  chega atrasado atropelaria uma campanha que a pessoa já escolheu, e a venda entraria creditada
 *  a quem ela não escolheu — sem nada na tela denunciando.
 *  ====================================================================== */
describe('modal de fechamento — a campanha da venda', () => {
  const CANAIS: OpcaoCanal[] = [
    { id: 7, nome: 'Panfleto Julho', ativo: true },
    { id: 9, nome: 'Vitrine', ativo: true }
  ];

  let fixture: ComponentFixture<ModalFechamento>;
  let c: ModalFechamento;

  function montar(canais: OpcaoCanal[] = CANAIS, detectado: number | null = null) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    fixture = TestBed.createComponent(ModalFechamento);
    c = fixture.componentInstance;
    fixture.componentRef.setInput('tipo', 'ganho');
    fixture.componentRef.setInput('canais', canais);
    fixture.componentRef.setInput('detectado', detectado);
    fixture.detectChanges();
  }

  it('sem canal detectado o campo nasce vazio — e vazio é uma resposta válida', () => {
    montar(CANAIS, null);
    expect(c.canalId()).toBeNull();
  });

  it('o detectado que chega DEPOIS pré-preenche o campo', () => {
    montar(CANAIS, null);

    // É o caso real: o modal abre e a lista chega na sequência.
    fixture.componentRef.setInput('detectado', 7);
    fixture.detectChanges();

    expect(c.canalId()).toBe(7);
  });

  /** ⚠️ O TESTE QUE IMPEDE O CRÉDITO ERRADO. */
  it('o detectado atrasado NÃO atropela a escolha do vendedor', () => {
    montar(CANAIS, null);

    c.canalId.set(9);                                  // o vendedor escolheu
    fixture.componentRef.setInput('detectado', 7);     // e só então o servidor respondeu
    fixture.detectChanges();

    expect(c.canalId()).toBe(9);
  });

  it('o canal escolhido sai no resultado, junto do valor', () => {
    montar(CANAIS, 7);

    let r: ResultadoFechamento | null = null;
    c.confirmado.subscribe(x => r = x);

    c.valor.set(1200);
    c.confirmar();

    expect(r).not.toBeNull();
    expect(r!.valor).toBe(1200);
    expect(r!.canalId).toBe(7);
  });

  /** Perda não tem campanha: creditar uma venda que não houve seria pior que não creditar nada. */
  it('em PERDA o canal sai nulo mesmo com campanha detectada', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    fixture = TestBed.createComponent(ModalFechamento);
    c = fixture.componentInstance;
    fixture.componentRef.setInput('tipo', 'perda');
    fixture.componentRef.setInput('canais', CANAIS);
    fixture.componentRef.setInput('detectado', 7);
    fixture.detectChanges();

    let r: ResultadoFechamento | null = null;
    c.confirmado.subscribe(x => r = x);

    c.motivo.set('achou caro');
    c.confirmar();

    expect(r!.canalId).toBeNull();
  });

  it('empresa sem campanha nenhuma não vê o seletor', () => {
    montar([], null);
    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('#canal')).toBeNull();
  });

  it('com campanhas, o seletor aparece com a opção "não sei" na frente', () => {
    montar(CANAIS, null);
    const opcoes = (fixture.nativeElement as HTMLElement).querySelectorAll('#canal option');

    // 2 campanhas + a opção vazia.
    expect(opcoes.length).toBe(3);
    expect(opcoes[0].textContent).toContain('Não sei');
  });

  /** Campanha encerrada só chega aqui quando ELA é a detectada — e aí precisa ser oferecida, com
   *  o rótulo dizendo o que é. Esconder apagaria a atribuição que o próprio sistema fez. */
  it('campanha encerrada aparece marcada como encerrada', () => {
    montar([{ id: 3, nome: 'Natal 2025', ativo: false }], 3);
    const opcoes = (fixture.nativeElement as HTMLElement).querySelectorAll('#canal option');
    expect(opcoes[1].textContent).toContain('encerrada');
  });
});
