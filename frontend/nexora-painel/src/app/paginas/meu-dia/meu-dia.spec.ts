import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AcaoDoDia, ContagemDoDia, PaginaDoDia } from '../../nucleo/modelos';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { POR_PAGINA } from '../../nucleo/paginacao/paginacao';
import { MeuDia } from './meu-dia';

/** FILTRO E PÁGINA NO MEU DIA — AGORA DO SERVIDOR (AUD-XX).
 *
 *  ===================== O QUE MUDOU, E O QUE ESTE ARQUIVO TRAVA =====================
 *  A tela recebia até 200 ações e fazia tudo: ordenava, filtrava, contava cada aba e paginava.
 *  Com 340 pendências o topo dizia "200 ações" — a contagem era da lista cortada.
 *
 *  Agora ela pede UMA página de UMA aba (`/meu-dia/pagina`) e recebe a ordem e as contagens de
 *  todas as abas. O que este arquivo trava é a tela não voltar a contar: os números exibidos são
 *  os do servidor, mesmo quando a página tem 20 itens e o dia tem 340.
 *  ================================================================================ */
describe('meu dia — filtro e paginação no servidor', () => {
  function acao(i: number, tipo: 'responder' | 'lembrete'): AcaoDoDia {
    return {
      tipo, id: i, contatoId: i, contatoNome: `Contato ${i}`, telefone: `55849000000${i}`,
      titulo: tipo === 'responder' ? 'Responder' : 'Follow-up',
      conversaId: tipo === 'responder' ? i : null,
      aguardandoDesde: tipo === 'responder' ? '2026-08-05T12:00:00Z' : null,
      minutosUteis: tipo === 'responder' ? 30 + i : null,
      esperaAcimaDaJanela: false,
      esperaDiasUteis: tipo === 'responder' ? 0 : null, conexaoNome: null,
      horaAlvo: tipo === 'lembrete' ? '09:00' : null,
      dataAlvo: tipo === 'lembrete' ? '2026-08-06' : null,
      atrasado: false
    } as AcaoDoDia;
  }

  /** O dia tem 340; a página traz 20. Qualquer número da tela que saia da LISTA diria 20. */
  const CONTAGENS: ContagemDoDia = { todas: 340, responder: 300, lembrete: 40, atrasadas: 5 };

  function pagina(itens: AcaoDoDia[], extra: Partial<PaginaDoDia> = {}): PaginaDoDia {
    return {
      itens, contagens: CONTAGENS, totalCount: 340, pagina: 1, tamanhoPagina: POR_PAGINA,
      totalPaginas: 17, ...extra
    };
  }

  const VINTE = Array.from({ length: 20 }, (_, i) => acao(i + 1, i < 15 ? 'responder' : 'lembrete'));

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
  let fixture: ComponentFixture<MeuDia>;
  let c: MeuDia;
  let primeiro: TestRequest;

  const pedidoDePagina = () => http.expectOne(r => r.url.endsWith('/meu-dia/pagina'));

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso }
      ]
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(MeuDia);
    c = fixture.componentInstance;
    fixture.detectChanges();

    primeiro = pedidoDePagina();
    http.match(r => r.url.includes('/painel/status')).forEach(r => r.flush({
      naoLidas: 0, aguardando: 0, whatsappConectado: true, trocouDeNumero: false,
      semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
      janelaHoraInicio: 0, janelaHoraFim: 24, janelaDiasSemana: 127, feriadosRecentes: []
    }));
  });

  afterEach(() => TestBed.resetTestingModule());

  function responderPrimeiro() {
    primeiro.flush(pagina(VINTE));
    fixture.detectChanges();
  }

  it('PEDE A PÁGINA AO SERVIDOR, com a aba e o tamanho', () => {
    expect(primeiro.request.params.get('filtro')).toBe('todas');
    expect(primeiro.request.params.get('pagina')).toBe('1');
    expect(primeiro.request.params.get('tamanho')).toBe(String(POR_PAGINA));
    responderPrimeiro();

    expect(c.acoes().length).toBe(20);
  });

  /** CONV-XX: o mesmo cliente pode esperar em dois números — o item diz em qual, quando o
   *  servidor manda (só com mais de um número na empresa). */
  it('O "RESPONDER" DIZ O NÚMERO quando o servidor o manda', () => {
    primeiro.flush(pagina([
      { ...acao(1, 'responder'), conexaoNome: 'Vendas' },
      acao(2, 'responder')
    ]));
    fixture.detectChanges();

    const detalhes = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.detalhe')]
      .map(e => e.textContent!.replace(/\s+/g, ' ').trim());
    expect(detalhes[0]).toContain('Contato 1 · pelo número Vendas');
    expect(detalhes[1]).not.toContain(' no ');
  });

  it('O CONTADOR DO TOPO É O DO SERVIDOR: 340, e não os 20 da página', () => {
    responderPrimeiro();

    expect(c.total()).toBe(340);
    expect(c.quantasConversas()).toBe(300);
    expect(c.quantosLembretes()).toBe(40);

    const sub = (fixture.nativeElement as HTMLElement).querySelector('.topo .sub')!.textContent!;
    expect(sub).toContain('340 ações para hoje');
  });

  it('AS PÍLULAS DAS ABAS LEEM AS CONTAGENS DO SERVIDOR', () => {
    responderPrimeiro();

    expect(c.quantasNoFiltro('todas')).toBe(340);
    expect(c.quantasNoFiltro('responder')).toBe(300);
    expect(c.quantasNoFiltro('lembrete')).toBe(40);
    expect(c.quantasNoFiltro('atrasadas')).toBe(5);
  });

  it('IR PARA UMA PÁGINA PEDE AQUELA PÁGINA, e trocar a aba volta para a 1', () => {
    responderPrimeiro();

    c.irPara(3);
    const p3 = pedidoDePagina();
    expect(p3.request.params.get('pagina')).toBe('3');
    p3.flush(pagina(VINTE, { pagina: 3 }));

    // Ficar na página 3 com outro recorte mostraria uma lista vazia com trabalho nas anteriores.
    c.trocarFiltro('lembrete');
    const lembretes = pedidoDePagina();
    expect(lembretes.request.params.get('filtro')).toBe('lembrete');
    expect(lembretes.request.params.get('pagina')).toBe('1');
    expect(c.pagina()).toBe(1);
  });

  it('clicar na aba já ativa não pede nada', () => {
    responderPrimeiro();

    c.trocarFiltro('todas');
    http.expectNone(r => r.url.endsWith('/meu-dia/pagina'));
  });

  /** ⚠️ CONCLUIR NÃO SUBTRAI NA TELA. O item anima saindo na hora; os números vêm do servidor,
   *  recarregados depois que ele confirma. Antes a tela tirava 1 do topo por conta própria. */
  it('CONCLUIR RECARREGA DO SERVIDOR, e o topo não desce por conta própria', () => {
    jasmine.clock().install();
    try {
      responderPrimeiro();

      const lembrete = c.acoes().find(a => a.tipo === 'lembrete')!;
      c.concluir(lembrete, new Event('click'));
      http.expectOne(r => r.url.endsWith(`/lembretes/${lembrete.id}/concluir`)).flush(null);
      http.match(r => r.url.includes('/painel/status')).forEach(r => r.flush({}));

      expect(c.total()).withContext('a tela subtraiu sozinha').toBe(340);

      jasmine.clock().tick(300);
      pedidoDePagina().flush(pagina(VINTE.filter(a => a !== lembrete),
        { contagens: { ...CONTAGENS, todas: 339, lembrete: 39 }, totalCount: 339 }));

      expect(c.total()).toBe(339);
      expect(c.quantosLembretes()).toBe(39);
    } finally {
      jasmine.clock().uninstall();
    }
  });

  /** Os dias de espera são os do SERVIDOR, pela janela da empresa (AUD-XX). 1.800 minutos úteis
   *  num dia de 10 horas são 3 dias; a conta antiga da tela (horas ÷ 12) dizia 2. */
  it('A ESPERA EM DIAS É A DO SERVIDOR, e não horas ÷ 12', () => {
    responderPrimeiro();

    const tresDias = { ...acao(1, 'responder'), minutosUteis: 1800, esperaDiasUteis: 3 };
    expect(c.espera(tresDias)).toBe('3 dias');

    const umDia = { ...acao(2, 'responder'), minutosUteis: 700, esperaDiasUteis: 1 };
    expect(c.espera(umDia)).toBe('1 dia');

    const horas = { ...acao(3, 'responder'), minutosUteis: 150, esperaDiasUteis: 0 };
    expect(c.espera(horas)).toBe('2h');
  });

  /** Concluiu-se o último item da última página: ela deixa de existir. A tela volta para a última
   *  que existe, pelo `totalPaginas` do servidor — e não por uma conta dela. */
  it('PÁGINA QUE DEIXOU DE EXISTIR VOLTA PARA A ÚLTIMA QUE EXISTE', () => {
    responderPrimeiro();

    c.irPara(17);
    pedidoDePagina().flush(pagina([], { pagina: 17, totalCount: 320, totalPaginas: 16 }));

    const volta = pedidoDePagina();
    expect(volta.request.params.get('pagina')).toBe('16');
  });
});
