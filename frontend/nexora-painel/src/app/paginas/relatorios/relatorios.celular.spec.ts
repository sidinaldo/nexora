import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { LARGURA_CELULAR } from '../telas-do-painel';
import { Relatorios } from './relatorios';

/** ===================== POR QUE ESTE ARQUIVO EXISTE =====================
 *  `paginas.celular.spec.ts` monta TODA tela em 390px — inclusive esta —, mas ali as requisições
 *  de lista são respondidas com vazio. Ou seja: o laço compartilhado mede o ESTADO VAZIO dos
 *  relatórios, e nunca a fileira de KPIs.
 *
 *  É o mesmo buraco que `etiquetas.celular.spec.ts` descreve, e aqui ele passou a importar: o
 *  CMP-1 acrescentou uma TERCEIRA LINHA em cada KPI, e `.kpis` é
 *  `auto-fit minmax(140px, 1fr)` — em 390px a fileira vira duas colunas de ~175px, que é a
 *  largura mais apertada em que essa linha existe no produto.
 *
 *  ⚠️ O VALOR AQUI É LONGO DE PROPÓSITO. "R$ 124.400,00" com `tabular-nums` é o pior caso
 *  realista; medir com "R$ 300" diria que cabe e não provaria nada.
 *  ======================================================================= */
describe('relatórios no celular — a linha de comparação', () => {
  const ind = (atual: number, anterior: number) => ({
    atual, anterior,
    variacaoAbsoluta: atual - anterior,
    variacaoPercentual: -28.6,
    tendencia: 'caiu' as const,
    avaliacao: 'pior' as const,
    anteriorDe: '2026-07-01',
    anteriorAte: '2026-07-31'
  });

  const VENDAS = {
    pontos: [
      {
        periodo: '2026-08-05', vendas: 87, faturamento: 124400, concluidas: 40,
        valorConcluido: 61200, canceladas: 9, valorCancelado: 14300
      }
    ],
    totais: {
      vendas: 87, faturamento: 124400, concluidas: 40, valorConcluido: 61200,
      canceladas: 9, valorCancelado: 14300, ticketMedio: 1430
    },
    comparativo: {
      vendas: ind(87, 122),
      faturamento: ind(124400, 174300),
      concluidas: ind(40, 56),
      valorConcluido: ind(61200, 85700),
      canceladas: ind(9, 13),
      valorCancelado: ind(14300, 20100),
      ticketMedio: ind(1430, 1428),
      de: '2026-08-01', ate: '2026-08-31', emAndamento: false
    }
  };

  /** A pesquisa PREENCHIDA, com números de três dígitos: "128 de 311" e "41 · 22 · 65" são o pior
   *  caso realista da seção, e é com eles desenhados que a fileira tem de caber em 390px. */
  const NPS_CELULAR = {
    totais: {
      enviadas: 311, respondidas: 128, expiradas: 160, canceladas: 3, aindaAbertas: 20,
      promotores: 41, neutros: 22, detratores: 65, nps: -18.8, taxaDeResposta: 41.2
    },
    distribuicao: Array.from({ length: 11 }, (_, nota) => ({ nota, quantas: 10 + nota })),
    comparativo: {
      nps: ind(-18.8, -12.5),
      respondidas: ind(128, 140),
      taxaDeResposta: ind(41.2, 44.9),
      promotores: ind(41, 50),
      de: '2026-08-01', ate: '2026-08-31', emAndamento: false
    }
  };

  let fixture: ComponentFixture<Relatorios>;
  let http: HttpTestingController;
  let palco: HTMLElement;

  function montar(emAndamento = false) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Padaria do Bairro'
      }
    } as never);

    palco = document.createElement('div');
    palco.style.width = `${LARGURA_CELULAR}px`;
    palco.style.overflow = 'hidden';
    document.body.appendChild(palco);

    fixture = TestBed.createComponent(Relatorios);
    http = TestBed.inject(HttpTestingController);
    palco.appendChild(fixture.nativeElement);

    fixture.detectChanges();

    const vendas = emAndamento
      ? { ...VENDAS, comparativo: { ...VENDAS.comparativo, ate: '2026-08-04', emAndamento: true } }
      : VENDAS;

    for (const r of http.match(() => true)) {
      const url = r.request.url;
      if (url.endsWith('/vendas')) r.flush(vendas);
      else if (url.endsWith('/opcoes')) r.flush({ responsaveis: [], etapas: [], motivosPerda: [] });
      else if (url.endsWith('/funil')) r.flush({ entradas: [], agora: [], trilhaComecaEm: null });
      else if (url.endsWith('/nps')) r.flush(NPS_CELULAR);
      else if (url.endsWith('/respostas')) r.flush({ total: 0, numeroPagina: 1, tamanho: 20, itens: [] });
      else if (url.endsWith('/recorrentes')) {
        r.flush({ total: 0, numeroPagina: 1, tamanho: 20, itens: [] });
      }
      else r.flush([]);
    }

    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
  }

  afterEach(() => {
    palco?.remove();
    TestBed.resetTestingModule();
  });

  /** ⚠️ MEDE O ELEMENTO, NÃO A PÁGINA, e a diferença foi medida antes de escrever isto: a tela de
   *  relatórios COM DADOS já transborda ~150px em 390px, e transborda EXATAMENTE o mesmo com a
   *  linha de comparação e sem ela. O defeito é anterior ao CMP-1 e está anotado — asserção sobre
   *  a página inteira aqui falharia por culpa de outra coisa, que é o jeito mais rápido de um
   *  teste virar ruído que se desliga. */
  function transbordoDe(seletor: string): number {
    const el = (fixture.nativeElement as HTMLElement).querySelector(seletor)!;
    return el.scrollWidth - el.clientWidth;
  }

  it('A FILEIRA DE KPIS COM A COMPARAÇÃO NÃO ANDA DE LADO', () => {
    montar();

    const raiz = fixture.nativeElement as HTMLElement;

    // ⚠️ NOVE, NAO CINCO: os cinco de vendas e os quatro da pesquisa pos-venda (NPS-1). Contar
    // so os de vendas deixaria a fileira nova sem medida nenhuma em 390px.
    const linhas = raiz.querySelectorAll('.comparado');
    expect(linhas.length).withContext('os nove KPIs, com a terceira linha').toBe(9);

    // E as DUAS fileiras — `transbordoDe` mede so a primeira que achar.
    const fileiras = [...raiz.querySelectorAll<HTMLElement>('.kpis')];
    expect(fileiras.length).toBe(2);

    for (const f of fileiras) {
      expect(f.scrollWidth - f.clientWidth)
        .withContext(`a fileira passa ${f.scrollWidth - f.clientWidth}px da largura que tem`)
        .toBeLessThanOrEqual(1);
    }
  });

  /** Cada KPI por dentro: `.kpis` é `auto-fit minmax(140px, 1fr)`, então em 390px a coluna tem
   *  ~175px — a largura mais apertada em que esta linha existe no produto. */
  it('NENHUM KPI ESTOURA POR DENTRO', () => {
    montar();

    const kpis = [...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.kpi-linha')];
    expect(kpis.length).toBe(9);

    for (const k of kpis) {
      expect(k.scrollWidth - k.clientWidth)
        .withContext(`"${k.querySelector('.kpi-rotulo')?.textContent?.trim()}": ` +
                     `${k.querySelector('.comparado')?.textContent?.trim()}`)
        .toBeLessThanOrEqual(1);
    }
  });

  /** O recorte do período em andamento divide o cabeçalho com o título e com "por dia · pela data
   *  de fechamento" — três textos numa linha de 390px. */
  it('O RECORTE DO PERÍODO EM ANDAMENTO NÃO ESTOURA O CABEÇALHO', () => {
    montar(true);

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('vs');
    expect(transbordoDe('.cartao-topo')).toBeLessThanOrEqual(1);
  });
});
