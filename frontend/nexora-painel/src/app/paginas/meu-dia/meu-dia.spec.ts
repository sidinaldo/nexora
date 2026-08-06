import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { AcaoDoDia } from '../../nucleo/modelos';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { POR_PAGINA } from '../../nucleo/paginacao/paginacao';
import { MeuDia } from './meu-dia';

/** FILTRO E PÁGINA NO MEU DIA.
 *
 *  ===================== POR QUE A TELA PRECISOU DISSO =====================
 *  Ela nasceu para uma lista curta — "o que fazer hoje" cabia numa olhada. Com base de verdade
 *  abriu com 100 ações, e aí deixa de ser um plano e vira um backlog: o vendedor rola, perde a
 *  posição e não sabe por onde começar.
 *
 *  O que este arquivo trava não é o recorte em si, é o que se erra ao implementá-lo: o contador
 *  do topo passar a contar a página, e o filtro deixar a pessoa numa página que não existe mais.
 *  ======================================================================== */
describe('meu dia — filtro e paginação', () => {
  function acao(i: number, tipo: 'responder' | 'lembrete', atrasado: boolean): AcaoDoDia {
    return {
      tipo, id: i, contatoId: i, contatoNome: `Contato ${i}`, telefone: `55849000000${i}`,
      titulo: tipo === 'responder' ? 'Responder' : 'Follow-up',
      conversaId: tipo === 'responder' ? i : null,
      aguardandoDesde: tipo === 'responder' ? '2026-08-05T12:00:00Z' : null,
      minutosUteis: tipo === 'responder' ? 30 + i : null,
      esperaAcimaDaJanela: false,
      horaAlvo: tipo === 'lembrete' ? '09:00' : null,
      atrasado
    } as AcaoDoDia;
  }

  // 30 conversas (10 atrasadas) + 15 lembretes (5 atrasados) = 45 ações, 3 páginas.
  const ACOES: AcaoDoDia[] = [
    ...Array.from({ length: 30 }, (_, i) => acao(i + 1, 'responder', i < 10)),
    ...Array.from({ length: 15 }, (_, i) => acao(i + 101, 'lembrete', i < 5))
  ];

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
  let c: MeuDia;

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
    const fixture = TestBed.createComponent(MeuDia);
    c = fixture.componentInstance;
    fixture.detectChanges();

    http.expectOne(r => r.url.includes('/meu-dia'))
      .flush({ acoes: ACOES, respondendo: 30, lembretes: 15 });
    http.match(r => r.url.includes('/painel/status')).forEach(r => r.flush({
      naoLidas: 0, aguardando: 0, whatsappConectado: true, trocouDeNumero: false,
      semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
      janelaHoraInicio: 0, janelaHoraFim: 24, janelaDiasSemana: 127, feriadosRecentes: []
    }));
    fixture.detectChanges();
  });

  afterEach(() => TestBed.resetTestingModule());

  it('A PÁGINA MOSTRA 20, NÃO AS 45', () => {
    expect(c.filtradas().length).toBe(45);
    expect(c.visiveis().length).toBe(POR_PAGINA);
    expect(c.totalPaginas()).toBe(3);

    c.irPara(3);
    expect(c.visiveis().length).withContext('última página').toBe(5);
  });

  it('O CONTADOR DO TOPO CONTA O DIA INTEIRO, NÃO A PÁGINA NEM O FILTRO', () => {
    // ===== O ERRO QUE ISTO IMPEDE =====
    // Se `total()` passasse a contar o recorte visível, o texto "100 ações para hoje" mudaria ao
    // trocar de aba — e o vendedor concluiria que o trabalho sumiu. O tamanho do dia é o tamanho
    // do dia.
    expect(c.total()).toBe(45);

    c.trocarFiltro('lembrete');
    expect(c.total()).withContext('o total mudou ao filtrar').toBe(45);
    expect(c.quantasConversas()).toBe(30);
    expect(c.quantosLembretes()).toBe(15);

    c.irPara(1);
    expect(c.total()).toBe(45);
  });

  it('o filtro recorta por tipo de trabalho e por atraso', () => {
    c.trocarFiltro('responder');
    expect(c.filtradas().length).toBe(30);
    expect(c.filtradas().every(a => a.tipo === 'responder')).toBeTrue();

    c.trocarFiltro('lembrete');
    expect(c.filtradas().length).toBe(15);

    // `atrasadas` corta por URGÊNCIA, cruzando os dois tipos — é outro eixo de propósito.
    c.trocarFiltro('atrasadas');
    expect(c.filtradas().length).toBe(15);
    expect(c.filtradas().every(a => a.atrasado)).toBeTrue();
    expect(new Set(c.filtradas().map(a => a.tipo)).size)
      .withContext('atrasados deveria cruzar conversa e lembrete').toBe(2);

    c.trocarFiltro('todas');
    expect(c.filtradas().length).toBe(45);
  });

  it('TROCAR O FILTRO VOLTA PARA A PÁGINA 1', () => {
    // Ficar na página 3 depois de filtrar para 15 itens mostraria uma lista vazia com trabalho
    // existindo nas páginas anteriores.
    c.irPara(3);
    expect(c.pagina()).toBe(3);

    c.trocarFiltro('lembrete');
    expect(c.pagina()).toBe(1);
    expect(c.visiveis().length).toBeGreaterThan(0);
  });

  it('a contagem de cada aba bate com o recorte dela', () => {
    // O número na pílula é o que evita clicar em cada aba para descobrir onde está o trabalho.
    expect(c.quantasNoFiltro('todas')).toBe(45);
    expect(c.quantasNoFiltro('responder')).toBe(30);
    expect(c.quantasNoFiltro('lembrete')).toBe(15);
    expect(c.quantasNoFiltro('atrasadas')).toBe(15);
  });

  it('clicar na aba já ativa não faz nada', () => {
    c.irPara(2);
    c.trocarFiltro('todas');   // já é o filtro corrente
    expect(c.pagina()).withContext('não deveria ter resetado a página').toBe(2);
  });
});
