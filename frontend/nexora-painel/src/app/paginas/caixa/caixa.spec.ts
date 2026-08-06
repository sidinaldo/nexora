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
