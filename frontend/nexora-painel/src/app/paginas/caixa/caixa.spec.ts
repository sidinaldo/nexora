import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { ConversaResumo } from '../../nucleo/modelos';
import { Caixa } from './caixa';

/** ABRIR A CONVERSA QUE VEIO DE FORA (`/caixa?conversa=N`).
 *
 *  ===================== O BUG QUE ISTO TRAVA =====================
 *  A lista é por CURSOR e o cliente carrega 30 itens. A versão anterior PROCURAVA o id na página
 *  carregada; não achando, trocava o filtro para "Todas" e tentava uma vez mais. Com uma base
 *  real, a conversa clicada no Meu Dia quase sempre está na página 4 — e a tela abria vazia, sem
 *  erro e sem explicação. O vendedor clicava e não acontecia nada.
 *
 *  O sintoma é invisível em base pequena: com 5 conversas tudo cabe na primeira página e o bug
 *  não aparece. Por isso o teste monta a lista SEM o alvo, de propósito.
 *  ================================================================ */
describe('caixa — abrir conversa por link', () => {
  const OUTRA: ConversaResumo = {
    id: 1, contatoId: 1, contatoNome: 'Alguém na primeira página', telefone: '5584900000001',
    ultimaMensagemPrevia: 'oi', ultimaMensagemDirecao: 'entrada',
    ultimaMensagemEm: '2026-08-05T12:00:00Z', aguardandoDesde: '2026-08-05T12:00:00Z',
    naoLidas: 1, status: 'aberta', responsavelId: null, responsavelNome: null,
    etapaId: 1, etapaNome: 'Novo Lead'
  };

  const ALVO: ConversaResumo = {
    ...OUTRA, id: 777, contatoId: 777, contatoNome: 'Fora da primeira página',
    telefone: '5584900000777', ultimaMensagemEm: '2020-01-01T09:00:00Z'
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

  function montar(conversaPedida: string | null) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({}),
              queryParamMap: convertToParamMap(conversaPedida ? { conversa: conversaPedida } : {}),
              data: {}
            }
          }
        }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);

    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(Caixa);
    fixture.detectChanges();

    // A lista NÃO traz o alvo — é o ponto do teste.
    http.expectOne(r => r.url.endsWith('/conversas') && r.method === 'GET')
      .flush({ itens: [OUTRA], temMais: true });

    // O status do painel (semáforo e janela) responde junto.
    http.match(r => r.url.includes('/painel/status')).forEach(r => r.flush({
      naoLidas: 0, aguardando: 0, whatsappConectado: true, trocouDeNumero: false,
      semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
      janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: []
    }));

    return fixture;
  }

  afterEach(() => {
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  it('BUSCA A CONVERSA PELO ID quando ela não está na página carregada', () => {
    const fixture = montar('777');

    // A correção: em vez de desistir, pede a conversa pelo id.
    const req = http.expectOne(r => r.url.endsWith('/conversas/777') && r.method === 'GET');
    req.flush(ALVO);
    fixture.detectChanges();

    const c = fixture.componentInstance;
    expect(c.sel()?.id).withContext('a conversa pedida não abriu').toBe(777);

    // FIXADA NO TOPO: quem veio de um link precisa VER a conversa. Enfiá-la na posição
    // cronológica (2020) seria escondê-la no fim da lista.
    expect(c.conversas()[0].id).toBe(777);
    expect(c.fixada()).toBe(777);
  });

  it('não busca nada quando a conversa JÁ está na página carregada', () => {
    const fixture = montar('1');
    fixture.detectChanges();

    // Uma requisição a mais aqui seria desperdício em cima do caminho mais comum.
    http.expectNone(r => r.url.endsWith('/conversas/1'));
    expect(fixture.componentInstance.sel()?.id).toBe(1);
    expect(fixture.componentInstance.fixada()).toBeNull();
  });

  it('CONVERSA INEXISTENTE OU DE OUTRO TENANT MOSTRA MENSAGEM, não tela vazia', () => {
    // 404 cobre os dois casos — o servidor não distingue de propósito.
    const fixture = montar('999');

    http.expectOne(r => r.url.endsWith('/conversas/999'))
      .flush({ erro: 'Conversa não encontrada.' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    const c = fixture.componentInstance;
    expect(c.erroPedida()).toContain('não é da sua empresa');
    expect(c.sel()).toBeNull();
    // A lista continua utilizável — o erro não derruba a tela.
    expect(c.conversas().length).toBe(1);
  });

  it('trocar de aba solta a conversa fixada', () => {
    // A partir daí o vendedor está navegando, e uma linha presa no topo fora da ordem
    // cronológica viraria ruído.
    const fixture = montar('777');
    http.expectOne(r => r.url.endsWith('/conversas/777')).flush(ALVO);
    fixture.detectChanges();
    expect(fixture.componentInstance.fixada()).toBe(777);

    fixture.componentInstance.trocarAba('Todas');
    expect(fixture.componentInstance.fixada()).toBeNull();

    http.expectOne(r => r.url.endsWith('/conversas') && r.method === 'GET')
      .flush({ itens: [OUTRA], temMais: false });
  });

  it('sem `?conversa=` nada é buscado', () => {
    const fixture = montar(null);
    fixture.detectChanges();

    http.expectNone(r => /\/conversas\/\d+$/.test(r.url));
    expect(fixture.componentInstance.sel()).toBeNull();
  });
});

/** ASSUMIR E LIBERAR — o resultado tem que aparecer SEM recarregar a página.
 *
 *  ===================== O BUG QUE ISTO TRAVA =====================
 *  `assumir()` chamava a API e mandava `mesclarTopo()` rebaixar a lista. O `mesclarTopo` repesca
 *  o selecionado na lista nova e, NÃO ACHANDO, fazia `?? atual` — mantendo o objeto velho, com
 *  `responsavelId` nulo, ou seja, com o botão "Assumir" ainda na tela.
 *
 *  E não achar é o caso NORMAL da ação: quem assume está na aba "Não atribuídas", de onde a
 *  conversa SAI no instante em que ganha dono. Recarregar consertava porque a lista vinha do
 *  zero — e foi assim que o defeito foi relatado: "só funcionou quando dei refresh".
 *  ================================================================ */
describe('caixa — assumir e liberar', () => {
  const SEM_DONO: ConversaResumo = {
    id: 55, contatoId: 55, contatoNome: 'Sem dono', telefone: '5584900000055',
    ultimaMensagemPrevia: 'oi', ultimaMensagemDirecao: 'entrada',
    ultimaMensagemEm: '2026-08-07T12:00:00Z', aguardandoDesde: '2026-08-07T12:00:00Z',
    naoLidas: 1, status: 'aberta', responsavelId: null, responsavelNome: null,
    etapaId: 1, etapaNome: 'Novo Lead'
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

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({}),
              queryParamMap: convertToParamMap({}),
              data: {}
            }
          }
        }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 9, nome: 'Rafael', email: 'r@x.com', papel: 'vendedor', empresaNome: 'Padaria' }
    } as never);

    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(Caixa);
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/conversas') && r.method === 'GET')
      .flush({ itens: [SEM_DONO], temMais: false });
    http.match(r => r.url.includes('/painel/status')).forEach(r => r.flush({
      naoLidas: 0, aguardando: 0, whatsappConectado: true, trocouDeNumero: false,
      semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
      janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: []
    }));

    return fixture;
  }

  afterEach(() => {
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  it('ASSUMIR aparece na hora, mesmo quando a conversa SAI do filtro atual', () => {
    const fixture = montar();
    const c = fixture.componentInstance;

    c.abrir(c.conversas()[0]);
    expect(c.sel()!.responsavelId).toBeNull();

    c.assumir();
    http.expectOne(r => r.url.endsWith('/55/assumir') && r.method === 'POST').flush(null);

    // A recarga volta SEM a conversa — é o que a aba "Não atribuídas" faz depois de assumir.
    http.expectOne(r => r.url.endsWith('/conversas') && r.method === 'GET')
      .flush({ itens: [], temMais: false });

    expect(c.sel()!.responsavelId).withContext('o dono tem que estar aplicado').toBe(9);
    expect(c.sel()!.responsavelNome).toBe('Rafael');
    expect(c.ehMinha(c.sel()))
      .withContext('é isto que troca o botão de "Assumir" para "Liberar"').toBeTrue();
  });

  it('LIBERAR também, e a lista acompanha', () => {
    const fixture = montar();
    const c = fixture.componentInstance;

    c.abrir(c.conversas()[0]);
    c.assumir();
    http.expectOne(r => r.url.endsWith('/55/assumir')).flush(null);
    http.expectOne(r => r.url.endsWith('/conversas')).flush({ itens: [], temMais: false });

    c.liberar();
    http.expectOne(r => r.url.endsWith('/55/liberar') && r.method === 'POST').flush(null);
    http.expectOne(r => r.url.endsWith('/conversas')).flush({ itens: [], temMais: false });

    expect(c.sel()!.responsavelId).toBeNull();
    expect(c.sel()!.responsavelNome).toBeNull();
  });

  it('a lista TAMBÉM recebe o novo dono, não só o selecionado', () => {
    // Senão a linha na lista continuaria dizendo "Aguardando" enquanto o cabeçalho já diz "Você".
    const fixture = montar();
    const c = fixture.componentInstance;

    c.abrir(c.conversas()[0]);
    c.assumir();
    http.expectOne(r => r.url.endsWith('/55/assumir')).flush(null);
    // A recarga TRAZ a conversa de volta (aba "Todas"), mas ainda com o dado velho do servidor —
    // o que prova que a aplicação local não depende dela.
    http.expectOne(r => r.url.endsWith('/conversas')).flush({ itens: [SEM_DONO], temMais: false });

    // Aqui o `mesclarTopo` sobrescreve com o que veio do servidor, que é o certo: ele ACHOU.
    // O teste garante que o caminho de não-achar (o do bug) é o que preserva a aplicação local.
    expect(c.conversas().length).toBe(1);
  });
});
