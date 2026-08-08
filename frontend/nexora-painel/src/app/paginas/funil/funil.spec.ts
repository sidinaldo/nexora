import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { ColunaFunil, ContatoCard } from '../../nucleo/modelos';
import { Funil } from './funil';

/** ARRASTAR E SOLTAR NO FUNIL (DES-4).
 *
 *  ===================== O BUG QUE ISTO TRAVA =====================
 *  As zonas de soltura eram as tiras `.solta` ENTRE os cards — faixas de poucos pixels. O espaço
 *  vazio abaixo dos cards, que é a maior parte de uma coluna com dois cards, não escutava nada:
 *  o `drop` nunca disparava e o card voltava sozinho, sem erro e sem explicação.
 *
 *  O sintoma é o pior possível numa interface: o vendedor tenta, falha, tenta de novo, e conclui
 *  que o kanban não funciona. Nada aparece no console.
 *
 *  ⚠️ Os eventos aqui são SINTÉTICOS porque o karma não arrasta nada de verdade. O que os testes
 *  provam é a MECÂNICA — quem escuta, se `preventDefault` foi chamado, onde o card entra dada a
 *  posição do cursor. O gesto real continua dependendo de teste manual em navegador.
 *  ================================================================ */
describe('funil — arrastar e soltar', () => {
  function card(id: number, nome: string, vendasEmAberto = 1): ContatoCard {
    return {
      id, nome, telefone: `558490000${id}`, ordemKanban: id * 1000,
      valor: 100, vendasEmAberto, responsavelId: null, responsavelNome: null,
      conversaId: null, aguardandoDesde: null, naoLidas: 0,
      ultimaMensagemEm: null, canalDoCiclo: null, versao: 1
    };
  }

  function coluna(
    etapaId: number, nome: string, cards: ContatoCard[], eGanho = false, concluidas = 0
  ): ColunaFunil {
    return {
      etapaId, nome, ordem: etapaId, cor: '#7FA88B', eGanho,
      total: cards.length, valorTotal: cards.length * 100, concluidas,
      contatos: cards, temMais: false
    };
  }

  const QUADRO = {
    colunas: [
      coluna(1, 'Novo Lead', [card(10, 'Ana'), card(11, 'Bruno')]),
      coluna(2, 'Proposta', []),                       // VAZIA — o caso do relato
      coluna(3, 'Venda', [], true)
    ]
  };

  class RealtimeFalso {
    conectado = signal(true);
    mensagemRecebida$ = new Subject<never>();
    conversaAberta$ = new Subject<never>();
    contatoCriado$ = new Subject<never>();
    statusMensagem$ = new Subject<never>();
    conexaoMudou$ = new Subject<never>();
    async conectar() { }
    desconectar() { }
  }

  let http: HttpTestingController;
  let fixture: ComponentFixture<Funil>;
  let c: Funil;

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'X' }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Funil);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      r.flush(r.request.url.includes('/funil') ? QUADRO : {
        naoLidas: 0, aguardando: 0, whatsappConectado: true, trocouDeNumero: false,
        semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
        janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: []
      });
    }
    fixture.detectChanges();
  }

  /** Um `DragEvent` que registra se o `preventDefault` foi chamado — é o que decide se a área é
   *  zona válida de soltura. Sem ele o `drop` não dispara, e em SILÊNCIO. */
  function evento(alvo: Element, clientY = 0, clientX = 0) {
    const e = {
      currentTarget: alvo,
      clientX, clientY,
      dataTransfer: { dropEffect: '', effectAllowed: '', setData: () => { } },
      preventDefaultChamado: false,
      preventDefault() { this.preventDefaultChamado = true; }
    };
    return e as unknown as DragEvent & { preventDefaultChamado: boolean };
  }

  const corpoDa = (etapaId: number) =>
    fixture.nativeElement.querySelector(`#etapa-${etapaId} .coluna-corpo`) as HTMLElement;

  afterEach(() => {
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  // ==================================================================== o alvo
  it('QUEM ESCUTA É O CORPO DA COLUNA, não as tiras entre os cards', () => {
    montar();

    const corpo = corpoDa(1);
    expect(corpo).withContext('a coluna precisa ter um corpo').toBeTruthy();

    // As tiras continuam existindo — como MARCADOR — e não podem interceptar o ponteiro.
    const marcador = corpo.querySelector('.solta') as HTMLElement;
    expect(marcador).toBeTruthy();
    expect(getComputedStyle(marcador).pointerEvents)
      .withContext('marcador que intercepta se põe entre o cursor e a coluna').toBe('none');

    // E o card arrastado também sai do caminho.
    const estilo = getComputedStyle(fixture.nativeElement.querySelector('.card') as HTMLElement);
    expect(estilo.cursor).toBe('grab');
  });

  it('DRAGOVER e DRAGENTER chamam preventDefault — sem isso o drop nunca dispara', () => {
    montar();
    c.aoIniciarArrasto(evento(document.body), QUADRO.colunas[0].contatos[0], 1);

    const entrar = evento(corpoDa(2));
    c.aoEntrarNaColuna(entrar, 2);
    expect(entrar.preventDefaultChamado)
      .withContext('sem preventDefault no dragenter a área não é zona válida').toBeTrue();

    const passar = evento(corpoDa(2));
    c.aoPassarSobre(passar, 2);
    expect(passar.preventDefaultChamado)
      .withContext('é a pegadinha nº 1 do DnD nativo').toBeTrue();
  });

  it('COLUNA VAZIA aceita o card, PELO EVENTO REAL do DOM', () => {
    // ⚠️ Este é o único teste que dispara evento de verdade, e existe por causa de uma mutação
    // que passou: com todos os testes chamando `c.aoSoltar(...)` direto, remover o `(drop)` do
    // template não quebrava nada — eles provavam a função, não a LIGAÇÃO. E a ligação era
    // exatamente o defeito relatado.
    montar();
    const alvo = QUADRO.colunas[0].contatos[0];
    c.aoIniciarArrasto(evento(document.body), alvo, 1);

    const corpo = corpoDa(2);
    const soltar = new DragEvent('drop', { bubbles: true, cancelable: true, clientY: 500 });
    corpo.dispatchEvent(soltar);

    // Moveu na tela ANTES da resposta (otimista).
    expect(c.colunas()[1].contatos.map(x => x.id)).toEqual([alvo.id]);
    expect(c.colunas()[0].contatos.map(x => x.id)).toEqual([11]);

    http.expectOne(r => r.url.includes('/mover') && r.method === 'POST').flush({ ordemKanban: 1 });
  });

  it('SOLTAR NO ESPAÇO VAZIO abaixo dos cards manda para o FIM da coluna', () => {
    // Era exatamente aqui que o card voltava sozinho: o espaço vazio não escutava nada.
    montar();
    const alvo = QUADRO.colunas[0].contatos[0];
    c.aoIniciarArrasto(evento(document.body), alvo, 1);

    // Y bem abaixo de qualquer card da coluna 1.
    c.aoSoltar(evento(corpoDa(1), 99_999), c.colunas()[0]);

    const pedido = http.expectOne(r => r.url.includes('/mover'));
    expect(pedido.request.body.aposContatoId)
      .withContext('no fim = depois do último card').toBe(11);
    pedido.flush({ ordemKanban: 1 });
  });

  // ==================================================================== onde entra
  it('a METADE do card decide se entra antes ou depois dele', () => {
    montar();
    const arrastado = QUADRO.colunas[0].contatos[0];
    c.aoIniciarArrasto(evento(document.body), arrastado, 1);

    const corpo = corpoDa(1);
    const cards = [...corpo.querySelectorAll<HTMLElement>('.card[data-id]')];

    // ⚠️ Sem esta asserção o teste passaria VAZIO: uma consulta que não acha card nenhum devolve
    // `null` como ponto de inserção, e `toBeNull()` daria verde sem medir nada. Foi o que a
    // primeira versão fez.
    expect(cards.length).withContext('o seletor precisa achar os cards').toBe(2);

    // Num runner headless nada é layoutado — `getBoundingClientRect` devolve zeros. As caixas são
    // forjadas para medir a REGRA (a metade), não o layout.
    const rect = (el: HTMLElement, top: number) => {
      el.getBoundingClientRect = () => ({ top, height: 40, bottom: top + 40 } as DOMRect);
    };
    rect(cards.find(el => el.dataset['id'] === '10')!, 0);      // Ana:   meio em 20
    rect(cards.find(el => el.dataset['id'] === '11')!, 100);    // Bruno: meio em 120

    // Acima do meio da Ana: vai para o TOPO.
    c.aoPassarSobre(evento(corpo, 10), 1);
    expect(c.alvo()?.aposContatoId).toBeNull();

    // Entre os dois meios: entra DEPOIS da Ana e antes do Bruno.
    c.aoPassarSobre(evento(corpo, 110), 1);
    expect(c.alvo()?.aposContatoId).toBe(10);

    // Abaixo do meio do Bruno: entra depois dele.
    c.aoPassarSobre(evento(corpo, 130), 1);
    expect(c.alvo()?.aposContatoId).toBe(11);
  });

  // ==================================================================== destaque
  it('O DESTAQUE NÃO PISCA ao passar sobre os cards filhos', () => {
    // Cada card dispara `dragenter`/`dragleave` da coluna. Sem contador de profundidade, o
    // destaque apaga no primeiro card e o estado se perde no meio do gesto.
    montar();
    c.aoIniciarArrasto(evento(document.body), QUADRO.colunas[0].contatos[0], 1);

    c.aoEntrarNaColuna(evento(corpoDa(2)), 2);       // entra na coluna
    c.aoPassarSobre(evento(corpoDa(2), 10), 2);
    expect(c.alvo()?.etapaId).toBe(2);

    c.aoEntrarNaColuna(evento(corpoDa(2)), 2);       // entra num card filho
    c.aoSairDaColuna(2);                              // sai do card, MAS continua na coluna
    expect(c.alvo()?.etapaId).withContext('ainda está dentro da coluna').toBe(2);

    c.aoSairDaColuna(2);                              // agora sim, saiu da coluna
    expect(c.alvo()).toBeNull();
  });

  // ==================================================================== o que não pode quebrar
  it('SOLTAR NA ETAPA DE GANHO abre o modal e NÃO move direto', () => {
    // A API recusa `mover` para etapa com e_ganho, de propósito. O card só sai do lugar depois
    // de o valor ser confirmado.
    montar();
    const alvo = QUADRO.colunas[0].contatos[0];
    c.aoIniciarArrasto(evento(document.body), alvo, 1);

    c.aoSoltar(evento(corpoDa(3), 50), c.colunas()[2]);

    expect(c.fechando()?.id).withContext('o modal de venda abre').toBe(alvo.id);
    http.expectNone(r => r.url.includes('/mover'));
    expect(c.colunas()[0].contatos.map(x => x.id))
      .withContext('o card fica onde estava até confirmar').toEqual([10, 11]);
  });

  it('CONFLITO (409) devolve o card e avisa, sem travar a tela', () => {
    montar();
    const alvo = QUADRO.colunas[0].contatos[0];
    c.aoIniciarArrasto(evento(document.body), alvo, 1);
    c.aoSoltar(evento(corpoDa(2), 500), c.colunas()[1]);

    // Moveu otimista...
    expect(c.colunas()[1].contatos.length).toBe(1);

    http.expectOne(r => r.url.includes('/mover'))
      .flush({ erro: 'Outro vendedor moveu este card.' }, { status: 409, statusText: 'Conflict' });

    // ...e voltou.
    expect(c.colunas()[1].contatos.length).withContext('o card volta ao lugar').toBe(0);
    expect(c.colunas()[0].contatos.map(x => x.id)).toEqual([10, 11]);
    expect(c.arrastando()).withContext('a tela não fica travada num arrasto').toBeNull();
  });

  it('soltar onde já estava não vira requisição', () => {
    montar();
    const bruno = QUADRO.colunas[0].contatos[1];
    c.aoIniciarArrasto(evento(document.body), bruno, 1);

    const corpo = corpoDa(1);
    const ana = [...corpo.querySelectorAll<HTMLElement>('.card[data-id]')]
      .find(el => el.dataset['id'] === '10')!;
    ana.getBoundingClientRect = () => ({ top: 0, height: 40, bottom: 40 } as DOMRect);

    c.aoSoltar(evento(corpo, 39), c.colunas()[0]);   // logo abaixo do meio da Ana = depois dela

    http.expectNone(r => r.url.includes('/mover'));
  });
});

// ================================================================ NEG-2
/** CONCLUIR NO FUNIL (NEG-2).
 *
 *  ===================== O QUE ISTO TRAVA =====================
 *  A coluna Venda acumulava para sempre: contato que comprou em março continuava lá em dezembro.
 *  Concluir tira o card SEM tirar o valor do faturamento — e é essa segunda metade que precisa
 *  estar visível na tela, senão ninguém conclui nada e a coluna volta a acumular.
 *
 *  Os testes atacam a fiação: qual rota é chamada, com que corpo, e se a caixa de seleção
 *  consegue ser marcada sem que o clique escorregue para o card (que abriria o contato).
 *  ============================================================ */
describe('funil — concluir venda (NEG-2)', () => {
  function card(id: number, nome: string, vendasEmAberto = 1): ContatoCard {
    return {
      id, nome, telefone: `558490000${id}`, ordemKanban: id * 1000,
      valor: 100, vendasEmAberto, responsavelId: null, responsavelNome: null,
      conversaId: null, aguardandoDesde: null, naoLidas: 0,
      ultimaMensagemEm: null, canalDoCiclo: null, versao: 1
    };
  }

  const QUADRO = {
    colunas: [
      {
        etapaId: 1, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', eGanho: false,
        total: 1, valorTotal: 100, concluidas: 0, contatos: [card(10, 'Ana')], temMais: false
      },
      {
        etapaId: 3, nome: 'Venda', ordem: 3, cor: '#7FA88B', eGanho: true,
        total: 2, valorTotal: 200, concluidas: 41,
        contatos: [card(20, 'Carla'), card(21, 'Davi', 2)], temMais: false
      }
    ] as ColunaFunil[]
  };

  class RealtimeFalso {
    conectado = signal(true);
    mensagemRecebida$ = new Subject<never>();
    conversaAberta$ = new Subject<never>();
    contatoCriado$ = new Subject<never>();
    statusMensagem$ = new Subject<never>();
    conexaoMudou$ = new Subject<never>();
    async conectar() { }
    desconectar() { }
  }

  let http: HttpTestingController;
  let fixture: ComponentFixture<Funil>;
  let c: Funil;

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'vendedor', empresaNome: 'X' }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Funil);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      r.flush(r.request.url.includes('/funil') ? QUADRO : {
        naoLidas: 0, aguardando: 0, whatsappConectado: true, trocouDeNumero: false,
        semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
        janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: []
      });
    }
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  /** ===================== O TESTE DA FIAÇÃO =====================
   *  Chama o handler pelo BOTÃO do template, não pelo método — uma versão que esqueça o
   *  `(click)` passaria num teste que invoca `c.concluirCard()` direto. Foi exatamente esse o
   *  furo encontrado no DES-4.
   *  ============================================================== */
  it('o botão Concluir do card manda o CONTATO, não a venda', () => {
    montar();

    const raiz = fixture.nativeElement as HTMLElement;
    const botoes = [...raiz.querySelectorAll<HTMLButtonElement>('.link-editar')]
      .filter(b => b.textContent!.trim() === 'Concluir');

    // Duas: uma por card da coluna de ganho. A coluna que não é de ganho mostra "Registrar venda".
    expect(botoes.length).withContext('o botão existe nos cards da coluna de ganho').toBe(2);

    botoes[0].click();

    const req = http.expectOne(r => r.url.endsWith('/vendas/concluir-do-contato'));
    expect(req.request.method).toBe('POST');
    // O card conhece o CONTATO; quem resolve as vendas em aberto dele é o servidor.
    expect(req.request.body).toEqual({ contatoIds: [20] });

    req.flush({ concluidas: 1 });
    // Recarrega o quadro: o card sai da coluna e o contador de concluídas sobe.
    for (const r of http.match(() => true)) r.flush(QUADRO);
  });

  it('a caixa de seleção NÃO abre o contato, e o lote manda todos os marcados', () => {
    montar();

    const raiz = fixture.nativeElement as HTMLElement;
    const caixas = [...raiz.querySelectorAll<HTMLInputElement>('.marca')];
    expect(caixas.length).withContext('só na coluna de ganho').toBe(2);

    // ⚠️ O card inteiro é clicável e arrastável. Sem `stopPropagation` no handler, marcar a
    // caixa navegaria para o contato — e ninguém chegaria a concluir nada em lote.
    let navegou = false;
    raiz.addEventListener('click', () => { navegou = true; });
    spyOn(c, 'abrirContato');

    caixas[0].click();
    caixas[1].click();
    fixture.detectChanges();

    expect(c.abrirContato).not.toHaveBeenCalled();
    expect(navegou).withContext('o clique não sobe até o card').toBeFalse();
    expect(c.marcadosNa(c.colunas()[1])).toBe(2);

    const barra = raiz.querySelector<HTMLElement>('.barra-lote')!;
    expect(barra).withContext('a barra de lote aparece com seleção').toBeTruthy();
    expect(barra.textContent).toContain('2 selecionados');

    barra.querySelector<HTMLButtonElement>('.btn-primario')!.click();

    const req = http.expectOne(r => r.url.endsWith('/vendas/concluir-do-contato'));
    expect(req.request.body).toEqual({ contatoIds: [20, 21] });

    req.flush({ concluidas: 3 });   // o Davi tinha 2 em aberto
    for (const r of http.match(() => true)) r.flush(QUADRO);

    expect(c.selecionados().size).withContext('a seleção limpa depois de concluir').toBe(0);
  });

  it('o cabeçalho da coluna de ganho mostra quantas já foram concluídas', () => {
    montar();

    // Sem este número, a coluna esvaziando pareceria perda de dado.
    const raiz = fixture.nativeElement as HTMLElement;
    const texto = raiz.querySelector('.coluna-ganho .concluidas')?.textContent;
    expect(texto).toContain('41');
  });

  it('contato com duas vendas em aberto mostra o número no card', () => {
    montar();

    // O quadro é montado por CONTATO: quem comprou duas vezes apareceria num card só.
    const raiz = fixture.nativeElement as HTMLElement;
    const selos = [...raiz.querySelectorAll<HTMLElement>('.card-valor .selo')];
    expect(selos.length).withContext('só quem tem mais de uma').toBe(1);
    expect(selos[0].textContent).toContain('2 vendas');
  });
});
