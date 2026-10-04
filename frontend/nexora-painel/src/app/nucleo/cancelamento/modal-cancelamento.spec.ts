import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ModalCancelamento, ResultadoCancelamento } from './modal-cancelamento';

/** O MODAL DO CANCELAMENTO (CAN-1).
 *
 *  ===================== O QUE ESTES TESTES PROTEGEM =====================
 *  Um botão servia a duas situações e tratava as duas igual: "registrei errado" (nada se perdeu)
 *  e "o cliente desistiu" (perda de verdade, com o dinheiro junto). A escolha não é cosmética —
 *  ela decide se a venda vira linha no relatório de perdas e se o card volta ao funil.
 *
 *  Duas coisas não podem quebrar:
 *
 *    1. o padrão é "registrei errado", que é o comportamento que sempre existiu. Quem não quiser
 *       mudar nada confirma direto;
 *    2. a opção que conta como perda EXIGE o motivo. Linha de perda sem motivo não ajuda ninguém
 *       a decidir nada — e é a mesma assimetria que o backend impõe.
 *  ====================================================================== */
describe('modal de cancelamento — registrei errado ou o cliente desistiu', () => {
  let fixture: ComponentFixture<ModalCancelamento>;
  let c: ModalCancelamento;

  function montar() {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    fixture = TestBed.createComponent(ModalCancelamento);
    c = fixture.componentInstance;
    fixture.componentRef.setInput('contatoNome', 'João');
    fixture.componentRef.setInput('valorFormatado', 'R$ 1.000,00');
    fixture.detectChanges();
  }

  /** ⚠️ ESTE É O TESTE QUE PROTEGE QUEM JÁ USAVA O PRODUTO. A opção nova não pode custar um passo
   *  a mais a quem só queria desfazer um lançamento errado — se o padrão fosse "desistiu", todo
   *  engano viraria uma perda inventada no relatório. */
  it('o padrão é "registrei errado", e confirma sem pedir nada', () => {
    montar();

    expect(c.foiPerda()).toBeFalse();
    expect(c.valido()).toBeTrue();

    let r: ResultadoCancelamento | null = null;
    c.confirmado.subscribe(x => r = x);
    c.confirmar();

    expect(r!.motivo).toBeNull();
  });

  it('"o cliente desistiu" não confirma sem o motivo', () => {
    montar();
    c.foiPerda.set(true);

    expect(c.valido()).toBeFalse();

    let chamou = false;
    c.confirmado.subscribe(() => chamou = true);
    c.confirmar();

    // ⚠️ `confirmar()` RECUSA SOZINHO, não só o botão fica desabilitado: Enter no campo de texto
    //    e um clique durante a animação chegam aqui sem passar pelo `[disabled]`.
    expect(chamou).toBeFalse();
  });

  it('com motivo, devolve o texto sem os espaços das pontas', () => {
    montar();
    c.foiPerda.set(true);
    c.motivo.set('  Achou caro  ');

    expect(c.valido()).toBeTrue();

    let r: ResultadoCancelamento | null = null;
    c.confirmado.subscribe(x => r = x);
    c.confirmar();

    // O relatório agrupa pelo texto EXATO: " Achou caro " e "Achou caro" virariam duas linhas
    // para o mesmo fato.
    expect(r!.motivo).toBe('Achou caro');
  });

  /** ⚠️ O CAMPO SÓ APARECE NA SEGUNDA OPÇÃO. Visível desde o início, ele convidaria a escrever um
   *  motivo em quem está só corrigindo um lançamento — e aí o engano entraria no relatório de
   *  perdas, que é exatamente o que este bloco existe para separar. */
  it('o campo de motivo só aparece depois de escolher "o cliente desistiu"', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    expect(raiz.querySelector('#motivo-cancelamento')).toBeNull();

    c.foiPerda.set(true);
    fixture.detectChanges();

    expect(raiz.querySelector('#motivo-cancelamento')).not.toBeNull();
  });

  /** As duas opções dizem o que vai acontecer, e isso é conteúdo — não enfeite. Depois do clique
   *  as duas são indistinguíveis na tela, então a diferença tem de estar visível ANTES. */
  it('cada opção avisa o que acontece com o card', () => {
    montar();
    const texto = (fixture.nativeElement as HTMLElement).textContent!;

    expect(texto).toContain('volta para o funil');
    expect(texto).toContain('Conta como perda');
  });
});
