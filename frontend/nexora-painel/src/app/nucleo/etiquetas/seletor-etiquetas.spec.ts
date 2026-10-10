import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { EtiquetaDto } from '../modelos';
import { AlvoEtiquetas, EtiquetasNoAlvo, SeletorEtiquetas } from './seletor-etiquetas';

/** O SELETOR DE ETIQUETAS.
 *
 *  ===================== O TESTE QUE IMPORTA É O DA CORRIDA =====================
 *  O componente recebe as etiquetas ATUAIS do contato por `input`, e elas chegam de uma
 *  requisição — ou seja, podem chegar DEPOIS de o modal abrir e depois de a pessoa já ter
 *  clicado. Semear o estado com `computed` faria a resposta atrasada desfazer a escolha dela.
 *
 *  É o mesmo defeito que o `modal-fechamento` trata com `if (d !== null && this.canalId() ===
 *  null)`, e aqui está tratado com o sinal `tocado`. `AS_ATUAIS_QUE_CHEGAM_DEPOIS…` é o teste
 *  que o segura.
 *  ============================================================================== */
describe('seletor de etiquetas', () => {
  const VOCABULARIO: EtiquetaDto[] = [
    { id: 1, nome: 'Revendedor', cor: '#2E7A56' },
    { id: 2, nome: 'Urgente', cor: '#B4552F' },
    { id: 3, nome: 'VIP', cor: '#A97A22' }
  ];

  let fixture: ComponentFixture<SeletorEtiquetas>;
  let componente: SeletorEtiquetas;

  function montar(atuais: EtiquetaDto[] = [], maximo = 8) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });

    fixture = TestBed.createComponent(SeletorEtiquetas);
    componente = fixture.componentInstance;

    fixture.componentRef.setInput('vocabulario', VOCABULARIO);
    fixture.componentRef.setInput('atuais', atuais);
    fixture.componentRef.setInput('maximo', maximo);
    fixture.detectChanges();
    return fixture;
  }

  function raiz(): HTMLElement { return fixture.nativeElement as HTMLElement; }

  function chips(): HTMLButtonElement[] {
    return [...raiz().querySelectorAll('.chip-botao')] as HTMLButtonElement[];
  }

  afterEach(() => TestBed.resetTestingModule());

  it('MOSTRA O VOCABULÁRIO INTEIRO, COM AS ATUAIS JÁ MARCADAS', () => {
    montar([VOCABULARIO[1]]);

    expect(chips().length).toBe(3);
    expect(chips().map(c => c.getAttribute('aria-pressed'))).toEqual(['false', 'true', 'false']);
  });

  // ==================================================================== a corrida
  it('AS ATUAIS QUE CHEGAM DEPOIS NÃO DESFAZEM O QUE A PESSOA JÁ MARCOU', () => {
    // Abre sem saber nada — a requisição das atuais ainda está no ar.
    montar([]);

    componente.alternar(1);
    expect([...componente.marcadas()]).toEqual([1]);

    // A resposta chega atrasada, dizendo que o contato tinha "Urgente".
    fixture.componentRef.setInput('atuais', [VOCABULARIO[1]]);
    fixture.detectChanges();

    // ⚠️ Sem o sinal `tocado`, aqui estaria [2] — e a pessoa veria a própria escolha sumir.
    expect([...componente.marcadas()])
      .withContext('a resposta atrasada não pode atropelar a escolha').toEqual([1]);
  });

  it('ENQUANTO NINGUÉM TOCA, AS ATUAIS AINDA SEMEIAM', () => {
    // O outro lado da mesma regra: chegar depois é normal, e sem interação tem de valer.
    montar([]);
    expect(componente.marcadas().size).toBe(0);

    fixture.componentRef.setInput('atuais', [VOCABULARIO[0], VOCABULARIO[2]]);
    fixture.detectChanges();

    expect([...componente.marcadas()].sort()).toEqual([1, 3]);
  });

  // ==================================================================== o teto
  it('NO TETO, O QUE NÃO ESTÁ MARCADO FICA INDISPONÍVEL', () => {
    // Desabilitar só o que está de fora, e não tudo: a saída do limite é DESMARCAR, e travar as
    // marcadas fecharia essa saída.
    montar([VOCABULARIO[0], VOCABULARIO[1]], 2);

    const [revendedor, urgente, vip] = chips();
    expect(vip.disabled).withContext('não marcada, e o teto chegou').toBeTrue();
    expect(revendedor.disabled).withContext('marcada — dá para desmarcar').toBeFalse();
    expect(urgente.disabled).toBeFalse();
  });

  it('O TETO NÃO DEIXA MARCAR ALÉM DELE NEM POR CÓDIGO', () => {
    montar([], 1);
    componente.alternar(1);
    componente.alternar(2);

    expect([...componente.marcadas()]).toEqual([1]);
  });

  // ==================================================================== saída
  it('CONFIRMAR DEVOLVE OS IDS E NÃO CHAMA A API', () => {
    // ⚠️ O componente NÃO emite requisição — devolve a escolha e quem abriu decide o que chamar.
    // É a primeira regra do `modal-fechamento`, e o que permite o quadro desfazer um estado
    // otimista quando o servidor recusa.
    montar([VOCABULARIO[0]]);

    let recebido: number[] | undefined;
    componente.confirmado.subscribe(ids => (recebido = ids));

    componente.alternar(3);
    componente.confirmar();

    expect(recebido).toBeDefined();
    expect([...recebido!].sort()).toEqual([1, 3]);
  });

  it('DESMARCAR TUDO E CONFIRMAR DEVOLVE LISTA VAZIA', () => {
    // Desmarcar a última é caso legítimo — a API trata lista vazia como "limpar".
    montar([VOCABULARIO[0]]);
    componente.alternar(1);

    let recebido: number[] | undefined;
    componente.confirmado.subscribe(ids => (recebido = ids));
    componente.confirmar();

    expect(recebido).toBeDefined();
    expect(recebido!.length).toBe(0);
  });

  it('ESC CANCELA', () => {
    montar();
    let cancelou = false;
    componente.cancelar.subscribe(() => (cancelou = true));

    componente.aoTeclar(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(cancelou).toBeTrue();
  });

  // ==================================================================== vocabulário vazio
  it('SEM VOCABULÁRIO, EXPLICA ONDE SE CRIA EM VEZ DE MOSTRAR NADA', () => {
    // Aplicar é de qualquer papel; CRIAR é do dono. Quem abre isto sem etiqueta cadastrada
    // precisa saber para onde olhar, e não pode simplesmente ver um modal vazio.
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    fixture = TestBed.createComponent(SeletorEtiquetas);
    componente = fixture.componentInstance;
    fixture.componentRef.setInput('vocabulario', []);
    fixture.detectChanges();

    expect(raiz().textContent).toContain('Nenhuma etiqueta cadastrada');
    expect(raiz().textContent).toContain('Configuração');

    const salvar = [...raiz().querySelectorAll('button')]
      .find(b => b.textContent?.trim() === 'Salvar') as HTMLButtonElement;
    expect(salvar.disabled).withContext('não há o que salvar').toBeTrue();
  });

  // ==================================================================== contrato do design system
  it('USA O `.overlay` DO DESIGN SYSTEM', () => {
    // ⚠️ `.overlay` é a ÚNICA exceção que `paginas.celular.spec.ts` aceita para algo que flutua
    // sobre o conteúdo. Um popover com `position: fixed` reprovaria a suíte inteira de celular.
    montar();
    expect(raiz().querySelector('.overlay > .modal')).not.toBeNull();
    expect(raiz().querySelector('.modal .modal-corpo')).not.toBeNull();
    expect(raiz().querySelector('.modal')?.getAttribute('role')).toBe('dialog');
  });

  // ==================================================================== onde marcar (BUG-XX)
  /** A pessoa e cada negociação têm etiquetas próprias. O seletor pergunta onde marcar, diz onde
   *  vai aparecer, guarda o rascunho de cada lugar e devolve só os que mudaram. */
  describe('onde marcar', () => {
    const ALVOS: AlvoEtiquetas[] = [
      { chave: 'pessoa', rotulo: 'Na pessoa', aviso: 'Vale para a Sofia em qualquer funil.', atuais: [VOCABULARIO[0]] },
      { chave: 'neg-12', rotulo: 'Pós-venda · Entrada', aviso: 'Só nesta negociação.', atuais: [] }
    ];

    function montarComAlvos(inicial = 'pessoa') {
      montar();
      fixture.componentRef.setInput('alvos', ALVOS);
      fixture.componentRef.setInput('alvoInicial', inicial);
      fixture.detectChanges();
    }

    it('ABRE NO ALVO PEDIDO, COM O AVISO E AS ETIQUETAS DELE', () => {
      montarComAlvos('neg-12');

      expect(componente.alvo()).toBe('neg-12');
      expect(raiz().querySelector('.onde')?.textContent).toContain('Só nesta negociação');
      expect(chips().map(c => c.getAttribute('aria-pressed'))).toEqual(['false', 'false', 'false']);
    });

    it('TROCAR DE ALVO NÃO PERDE O QUE FOI MARCADO NO OUTRO, E SÓ O QUE MUDOU É DEVOLVIDO', () => {
      montarComAlvos('pessoa');
      let devolvido: EtiquetasNoAlvo[] | null = null;
      componente.confirmadoNosAlvos.subscribe(m => devolvido = m);

      componente.escolherAlvo('neg-12');
      componente.alternar(2);              // Urgente na negociação
      componente.escolherAlvo('pessoa');   // volta: a pessoa continua como estava
      expect(componente.marcadas().has(1)).toBeTrue();
      componente.escolherAlvo('neg-12');
      expect(componente.marcadas().has(2)).withContext('o rascunho da negociação ficou').toBeTrue();

      componente.confirmar();

      expect(devolvido!).toEqual([{ chave: 'neg-12', ids: [2] }]);
    });

    it('SEM MUDANÇA, DEVOLVE A LISTA VAZIA', () => {
      montarComAlvos('pessoa');
      let devolvido: EtiquetasNoAlvo[] | null = null;
      componente.confirmadoNosAlvos.subscribe(m => devolvido = m);

      componente.alternar(2);
      componente.alternar(2);   // marcou e desmarcou: igual ao que estava
      componente.confirmar();

      expect(devolvido!).toEqual([]);
    });
  });
});
