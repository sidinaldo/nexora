import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { Relatorios } from './relatorios';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';

/** ===================== RELATÓRIOS (BLOCO 14) =====================
 *
 *  O que estes testes travam não é o desenho da tela — é o que o prompt proíbe explicitamente:
 *
 *   1. rotular como "no período" um dado que é FOTO ATUAL;
 *   2. montar o CSV no browser em vez de buscar do servidor;
 *   3. deixar o vendedor pedir o número de outro vendedor.
 *
 *  O item 3 tem teste de verdade na API (`RelatoriosDbTests`); aqui se prova só que a tela não
 *  OFERECE o caminho — que é cortesia, não proteção, e o comentário do componente diz isso.
 *  ============================================================== */
describe('relatórios (bloco 14)', () => {
  const OPCOES = {
    responsaveis: [{ id: 1, nome: 'Ana' }, { id: 2, nome: 'Bruno' }],
    // ⚠️ DOIS FUNIS, COM NOME DE ETAPA REPETIDO — é a condição do defeito (FUN-1). Com um funil
    //    só, qualquer implementação passa: não há o que distinguir. Os nomes são os de produção,
    //    colidindo só na maiúscula.
    etapas: [
      { id: 10, nome: 'Novo Lead', pipelineId: 1, pipelineNome: 'Vendas' },
      { id: 11, nome: 'Proposta', pipelineId: 1, pipelineNome: 'Vendas' },
      { id: 12, nome: 'Venda', pipelineId: 1, pipelineNome: 'Vendas' },
      { id: 20, nome: 'Novo lead', pipelineId: 2, pipelineNome: 'Atacado' },
      { id: 21, nome: 'Proposta', pipelineId: 2, pipelineNome: 'Atacado' }
    ],
    motivosPerda: ['preço', 'prazo']
  };

  /** Um indicador comparativo como o servidor o manda (CMP-1). */
  const ind = (
    atual: number, anterior: number,
    tendencia: 'subiu' | 'caiu' | 'estavel',
    avaliacao: 'melhor' | 'pior' | 'neutro',
    variacaoPercentual: number | null
  ) => ({
    atual, anterior,
    variacaoAbsoluta: atual - anterior,
    variacaoPercentual, tendencia, avaliacao,
    anteriorDe: '2026-07-01', anteriorAte: '2026-07-31'
  });

  /** ⚠️ "CANCELADO" SUBINDO É `pior`, e é o indicador que prova a regra de cor: seta para CIMA e
   *  vermelho. O teste do outro sentido (caindo = verde) sobrescreve este valor. */
  const COMPARATIVO = {
    vendas: ind(2, 2, 'estavel', 'neutro', 0),
    faturamento: ind(1000, 1400, 'caiu', 'pior', -28.6),
    concluidas: ind(1, 0, 'subiu', 'melhor', null),
    valorConcluido: ind(400, 0, 'subiu', 'melhor', null),
    canceladas: ind(1, 0, 'subiu', 'pior', null),
    valorCancelado: ind(300, 100, 'subiu', 'pior', 200),
    ticketMedio: ind(500, 400, 'subiu', 'melhor', 25),
    de: '2026-08-01', ate: '2026-08-31', emAndamento: false
  };

  const VENDAS = {
    pontos: [
      { periodo: '2026-08-05', vendas: 2, faturamento: 1000, concluidas: 1, valorConcluido: 400, canceladas: 0, valorCancelado: 0, mediaFaturamento: null },
      { periodo: '2026-08-06', vendas: 0, faturamento: 0, concluidas: 0, valorConcluido: 0, canceladas: 1, valorCancelado: 300, mediaFaturamento: null }
    ],
    totais: {
      vendas: 2, faturamento: 1000, concluidas: 1, valorConcluido: 400,
      canceladas: 1, valorCancelado: 300, ticketMedio: 500
    },
    comparativo: COMPARATIVO
  };

  // ⚠️ DOIS FUNIS, COM "PROPOSTA" NOS DOIS — a condição do defeito (FUN-1). E note as `ordem`
  //    repetidas (1, 2 em cada): é assim no banco, porque `uq_etapas_ordem` é POR PIPELINE. Uma
  //    carga com ordem única pela empresa esconderia metade do problema.
  const V = (etapaId: number, nome: string, ordem: number) =>
    ({ etapaId, nome, ordem, cor: '#7FA88B', pipelineId: 1, pipelineNome: 'Vendas' });
  const A = (etapaId: number, nome: string, ordem: number) =>
    ({ etapaId, nome, ordem, cor: '#14432F', pipelineId: 2, pipelineNome: 'Atacado' });

  // As duas perguntas na MESMA linha, juntadas no servidor (AUD-XX).
  const FUNIL = {
    etapas: [
      { ...V(10, 'Novo Lead', 1), entradas: 42, contatosAgora: 40, valorAgora: 8000 },
      { ...V(11, 'Proposta', 2), entradas: 19, contatosAgora: 3, valorAgora: 900 },
      { ...A(20, 'Novo lead', 1), entradas: 16, contatosAgora: 12, valorAgora: 4000 },
      { ...A(21, 'Proposta', 2), entradas: 6, contatosAgora: 2, valorAgora: 600 }
    ],
    trilhaComecaEm: '2026-08-07T10:00:00Z' as string | null
  };


  /** A pesquisa como o servidor a manda (NPS-1). Sete enviadas, quatro respondidas, uma ainda no
   *  prazo: NPS +25, taxa 57,1%. As ONZE notas, como o servidor garante. */
  const NPS_FIXTURE = {
    totais: {
      enviadas: 7, respondidas: 4, expiradas: 1, canceladas: 1, aindaAbertas: 1,
      promotores: 2, neutros: 1, detratores: 1, nps: 25, taxaDeResposta: 57.1
    },
    distribuicao: Array.from({ length: 11 }, (_, nota) =>
      ({ nota, quantas: nota === 10 ? 2 : nota === 7 || nota === 3 ? 1 : 0 })),
    comparativo: {
      nps: ind(25, 10, 'subiu', 'melhor', 150),
      respondidas: ind(4, 3, 'subiu', 'melhor', 33.3),
      taxaDeResposta: ind(57.1, 60, 'caiu', 'pior', -4.8),
      promotores: ind(2, 1, 'subiu', 'melhor', 100),
      de: '2026-08-01', ate: '2026-08-31', emAndamento: false
    }
  };

  let http: HttpTestingController;
  let fixture: ComponentFixture<Relatorios>;
  let c: Relatorios;

  /** Duas respostas: uma de quem voltou, outra de quem não voltou — as duas formas da coluna. */
  const RESPOSTAS = {
    totalCount: 2, pagina: 1, tamanhoPagina: 20, totalPaginas: 1,
    itens: [
      {
        pesquisaId: 1, contatoId: 7, cliente: 'Maria', nota: 10, dataResposta: '2026-08-05T15:00:00Z',
        comentario: 'Ótimo', responsavelId: 1, responsavel: 'Ana',
        ultimaCompraEm: '2026-09-15T12:00:00Z', comprouDeNovoEm: '2026-09-15T12:00:00Z'
      },
      {
        pesquisaId: 2, contatoId: 8, cliente: 'João', nota: 3, dataResposta: '2026-08-06T15:00:00Z',
        comentario: null, responsavelId: null, responsavel: null,
        ultimaCompraEm: '2026-07-20T12:00:00Z', comprouDeNovoEm: null
      }
    ]
  };

  function montar(
    papel: 'dono' | 'vendedor' = 'dono', opcoes = OPCOES, funil = FUNIL,
    nps: unknown = NPS_FIXTURE, respostas: unknown = RESPOSTAS
  ) {
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
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel, permissoes: PERMISSOES_DE[papel], empresaNome: 'X' }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Relatorios);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      const url = r.request.url;
      if (url.endsWith('/opcoes')) r.flush(opcoes);
      else if (url.endsWith('/vendas')) r.flush(VENDAS);
      else if (url.endsWith('/funil')) r.flush(funil);
      else if (url.endsWith('/nps')) r.flush(nps as object);
      else if (url.endsWith('/respostas')) r.flush(respostas as object);
      else if (url.endsWith('/recorrentes')) r.flush({ totalCount: 0, pagina: 1, tamanhoPagina: 20, totalPaginas: 1, itens: [] });
      else r.flush([]);
    }
    fixture.detectChanges();
  }

  /** ===================== OS SETE SECUNDÁRIOS COMEÇAM FECHADOS (REL-1) =====================
   *  Responsável, Origem, Etapa, Situação, Motivo e os dois de Valor saíram da barra e foram para
   *  um bloco recolhido. Fechado, `querySelector` não acha nenhum deles.
   *
   *  ⚠️ OS TESTES NÃO MUDARAM DE ASSUNTO, mudaram de caminho: continuam medindo o que mediam, só
   *  precisam abrir a gaveta antes. Trocá-los por asserções sobre o signal esconderia justamente o
   *  que eles existem para pegar — que o `<select>` chega à TELA no estado certo.
   *  ======================================================================================= */
  function abrirMaisFiltros() {
    c.maisFiltros.set(true);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  /** A conversão chega PRONTA, de 0 a 100 (AUD-XX). 66,67 com 10 vendas: se a tela voltar a
   *  multiplicar por 100 ou a dividir vendas por algo, o número que ela mostra é outro. */
  it('A CONVERSÃO DO VENDEDOR É A DO SERVIDOR, e sem nada decidido é "—"', () => {
    montar();
    c.vendedores.set([
      { usuarioId: 1, nome: 'Ana', leadsAtendidos: 30, vendas: 10, valor: 5000, ticketMedio: 500, conversaoPercentual: 66.67 },
      { usuarioId: 2, nome: 'Bruno', leadsAtendidos: 4, vendas: 0, valor: 0, ticketMedio: 0, conversaoPercentual: null }
    ]);
    fixture.detectChanges();

    const linhas = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tr')]
      .map(tr => tr.textContent!.replace(/\s+/g, ' '));

    expect(linhas.find(t => t.includes('Ana'))).toContain('66,67%');
    expect(linhas.find(t => t.includes('Bruno'))).toContain('—');
    expect(linhas.find(t => t.includes('Bruno'))).not.toContain('0%');
  });

  // ============================================================ REL-1 · a barra de filtros
  /** ===================== CINCO FAIXAS ANTES DO PRIMEIRO NÚMERO =====================
   *  Eram 11 campos numa grade `auto-fill minmax(190px)`: a 1440px dá cinco colunas, logo três
   *  faixas — mais os atalhos em cima e as ações embaixo. A tela começava com um formulário.
   *
   *  ⚠️ O CONTADOR É O QUE TORNA O RECOLHIMENTO HONESTO. Fechado, ele é a única coisa dizendo que
   *  os números estão recortados; sem ele o dono lê um relatório filtrado achando que é o total.
   *  ================================================================================ */
  it('OS FILTROS SECUNDÁRIOS COMEÇAM FECHADOS, e o botão não conta nada', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    expect(raiz.querySelector('.mais-filtros')).toBeNull();
    expect(raiz.querySelector('#f-responsavel')).toBeNull();

    const botao = raiz.querySelector('.abre-filtros')!;
    expect(botao.textContent!.trim()).toBe('Mais filtros');
    expect(botao.getAttribute('aria-expanded')).toBe('false');
  });

  /** Os sete, NOMINALMENTE. Contar quantos abriram deixaria passar a troca de um pelo outro — e a
   *  lista é justamente o que impede um campo ficar para trás numa mudança da barra. */
  it('ABRIR O BLOCO REVELA OS SETE FILTROS SECUNDÁRIOS', () => {
    montar();
    abrirMaisFiltros();

    const rotulos = [...(fixture.nativeElement as HTMLElement)
      .querySelectorAll('.mais-filtros label.campo > span')]
      .map(e => e.textContent!.trim());

    expect(rotulos).toEqual([
      'Responsável', 'Origem', 'Etapa', 'Situação da venda', 'Motivo de perda',
      'Valor da venda — de', 'Valor da venda — até'
    ]);
  });

  /** ===================== "AGRUPAR POR" NÃO É FILTRO =====================
   *  Ele não recorta dado nenhum: muda a granularidade da curva ao lado. Recolhido junto com os
   *  outros, a pessoa trocaria o período e não entenderia por que o gráfico mudou de forma.
   *
   *  ⚠️ DE/ATÉ PELO MESMO MOTIVO: sem eles, nenhum número da página tem significado.
   *  ====================================================================== */
  it('"AGRUPAR POR", DE E ATÉ NÃO SE ESCONDEM', () => {
    montar();

    const visiveis = [...(fixture.nativeElement as HTMLElement)
      .querySelectorAll('.linha-filtros > label.campo > span')]
      .map(e => e.textContent!.trim());

    expect(visiveis).toEqual(['De', 'Até', 'Agrupar por']);
  });

  /** ⚠️ CONTA FILTRO ATIVO, NÃO CAMPO ESCONDIDO. São sempre sete campos lá dentro; um contador de
   *  campos diria "7" para sempre e não informaria nada. */
  it('O CONTADOR CONTA FILTRO ATIVO, e some quando não há nenhum', () => {
    montar();
    const botao = () => (fixture.nativeElement as HTMLElement)
      .querySelector('.abre-filtros')!.textContent!.trim();

    expect(c.filtrosAtivos()).toBe(0);

    c.origem.set('site');
    c.motivoPerda.set('preço');
    fixture.detectChanges();

    expect(c.filtrosAtivos()).toBe(2);
    expect(botao()).toContain('(2)');

    // E some de novo — senão o aviso vira ruído permanente e ninguém mais o lê.
    c.origem.set(null);
    c.motivoPerda.set(null);
    fixture.detectChanges();

    expect(botao()).toBe('Mais filtros');
  });

  // ============================================================ REL-1 · a venda no período
  /** ===================== CURVA, NÃO FILEIRA DE COLUNAS =====================
   *
   *  Eram barras verticais. Num período de 30 dias isso dá 30 colunas finas com picos isolados, e a
   *  continuidade — que é a pergunta que se faz a um relatório de período ("está subindo?") — some
   *  entre elas.
   *
   *  ⚠️ O TESTE OLHA O CARTÃO DE VENDAS, NÃO A PÁGINA. A tela tem outros gráficos de barras (o
   *  funil, a origem), e procurar `app-grafico-barras` no documento inteiro mediria outra coisa —
   *  passaria verde com a barra de volta aqui dentro.
   *  ========================================================================= */
  // ============================================================ CMP-1 · a comparação
  function cartaoDeVendas(): HTMLElement {
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('section.cartao')]
      .find(s => s.querySelector('h2')?.textContent?.includes('Vendas no período'))!;
  }

  /** O KPI pelo RÓTULO, e não pela posição: reordenar a fileira não pode trocar o que se mede. */
  function kpi(rotulo: string): HTMLElement {
    return [...cartaoDeVendas().querySelectorAll<HTMLElement>('.kpi-linha')]
      .find(k => k.querySelector('.kpi-rotulo')?.textContent?.includes(rotulo))!;
  }

  function comparado(rotulo: string): HTMLElement {
    return kpi(rotulo).querySelector<HTMLElement>('.comparado')!;
  }

  it('TODOS OS CINCO KPIS GANHAM A LINHA DE COMPARAÇÃO', () => {
    montar();

    expect(cartaoDeVendas().querySelectorAll('.comparado').length).toBe(5);
    expect(comparado('Faturamento').textContent).toContain('28,6%');
    expect(comparado('Faturamento').textContent).toContain('em jul');
  });

  /** ===================== A COR SEGUE A AVALIAÇÃO, A SETA SEGUE O MOVIMENTO =====================
   *
   *  ⚠️ OS DOIS SENTIDOS NO MESMO ASSUNTO, e é o par que prova. "Cancelado" subindo é seta para
   *  CIMA e vermelho; caindo é seta para BAIXO e VERDE. Uma versão que colasse a cor na seta
   *  passaria no primeiro caso e falharia no segundo — e em produção pintaria de vermelho a melhor
   *  notícia do mês.
   *  ============================================================================================ */
  it('CANCELADO SUBINDO É VERMELHO, COM SETA PARA CIMA', () => {
    montar();

    const linha = comparado('Cancelado');
    expect(linha.classList).toContain('av-pior');
    expect(linha.textContent?.trim().startsWith('↑')).withContext(linha.textContent ?? '').toBeTrue();
  });

  it('CANCELADO CAINDO É VERDE, COM SETA PARA BAIXO', () => {
    montar();

    c.vendas.set({
      ...VENDAS,
      comparativo: { ...COMPARATIVO, valorCancelado: ind(100, 300, 'caiu', 'melhor', -66.7) }
    });
    fixture.detectChanges();

    const linha = comparado('Cancelado');
    expect(linha.classList).withContext('cair é BOM num indicador que deve descer').toContain('av-melhor');
    expect(linha.textContent?.trim().startsWith('↓')).withContext(linha.textContent ?? '').toBeTrue();
  });

  /** ⚠️ ZERO ANTES NÃO É −100%, e a frase é POR INDICADOR: "nada concluído" aqui, "sem
   *  cancelamento" no cancelado. Uma palavra genérica ("novo") diria a coisa errada no cancelado —
   *  mês sem cancelamento não é novidade a celebrar. */
  it('SEM NADA ANTES, NÃO HÁ PERCENTUAL', () => {
    montar();

    const linha = comparado('Já concluído');
    expect(linha.textContent).toContain('nada concluído em jul');
    expect(linha.textContent).not.toContain('%');

    expect(comparado('Cancelado').textContent).not.toContain('sem cancelamento');
  });

  /** ===================== O MÊS SÓ É DITO QUANDO É UM MÊS =====================
   *
   *  ⚠️ ACHADO NA TELA, NÃO NO TESTE. Com o atalho de "30 dias" o período anterior vira
   *  08/ago–05/set — e a primeira versão nomeava só o mês do FIM: saiu "sem venda em set" para uma
   *  janela que era quase toda de agosto. A frase culpava o mês errado.
   *
   *  Agora a janela que atravessa a virada não nomeia mês nenhum; as datas exatas seguem no
   *  `title`, que é onde cabem.
   *  ========================================================================== */
  it('O PERÍODO ANTERIOR QUE ATRAVESSA MESES NÃO NOMEIA MÊS', () => {
    montar();

    const atravessa = { ...ind(1000, 1400, 'caiu', 'pior', -28.6), anteriorDe: '2026-08-08', anteriorAte: '2026-09-05' };

    c.vendas.set({ ...VENDAS, comparativo: { ...COMPARATIVO, faturamento: atravessa } });
    fixture.detectChanges();

    const texto = comparado('Faturamento').textContent ?? '';
    expect(texto).withContext(texto).not.toContain('em set');
    expect(texto).toContain('28,6%');

    // E o caso-zero diz "no período anterior" em vez de culpar um mês.
    c.vendas.set({
      ...VENDAS,
      comparativo: {
        ...COMPARATIVO,
        valorConcluido: { ...atravessa, atual: 400, anterior: 0, variacaoPercentual: null }
      }
    });
    fixture.detectChanges();

    expect(comparado('Já concluído').textContent).toContain('nada concluído no período anterior');
  });

  /** Mesmo número nos dois períodos: nem seta, nem cor. */
  it('IGUAL AO PERÍODO ANTERIOR NÃO GANHA SETA', () => {
    montar();

    const linha = comparado('Vendas');
    expect(linha.classList).toContain('av-neutro');
    expect(linha.textContent).toContain('igual a 2 em jul');
  });

  /** ⚠️ O RECORTE SÓ APARECE EM PERÍODO EM ANDAMENTO — e PRECISA aparecer: sem ele, quatro dias
   *  contra trinta pareceriam uma queda de 87% no dia 4 de todo mês. */
  it('O RECORTE DOS DOIS PERÍODOS APARECE SÓ COM O MÊS EM ANDAMENTO', () => {
    montar();

    expect(cartaoDeVendas().textContent).not.toContain('vs');

    c.vendas.set({
      ...VENDAS,
      comparativo: { ...COMPARATIVO, ate: '2026-08-04', emAndamento: true }
    });
    fixture.detectChanges();

    expect(cartaoDeVendas().textContent).toContain('01/ago–04/ago vs 01/jul–31/jul');
  });

  /** A tela nova contra um servidor antigo: sem comparativo, nenhuma linha — e nada quebra. */
  it('SEM COMPARATIVO NO PAYLOAD, A TELA NAO MOSTRA A LINHA', () => {
    montar();

    c.vendas.set({ pontos: VENDAS.pontos, totais: VENDAS.totais });
    fixture.detectChanges();

    expect(cartaoDeVendas().querySelectorAll('.comparado').length).toBe(0);
    expect(cartaoDeVendas().textContent).toContain('Faturamento');
  });

  it('O CARTÃO DE VENDAS DESENHA ÁREA, NÃO BARRA', () => {
    montar();

    const cartao = [...(fixture.nativeElement as HTMLElement).querySelectorAll('section.cartao')]
      .find(s => s.querySelector('h2')?.textContent?.includes('Vendas no período'))!;

    expect(cartao.querySelector('app-grafico-linha')).not.toBeNull();
    expect(cartao.querySelector('app-grafico-barras')).toBeNull();

    // E a série sai dos pontos da API, com o faturamento de cada um.
    expect(c.serieVendas().map(p => p.valor)).toEqual([1000, 0]);
  });

  /** ===================== A MÉDIA MÓVEL NÃO VALE PARA MÊS =====================
   *
   *  Uma janela de 7 sobre 12 pontos mensais não suaviza: ela achata mais de meio ano num traço
   *  reto, e o tracejado passa a contar uma história que o dado não tem.
   *
   *  ⚠️ OS DOIS LADOS, e é o par que prova. Afirmar só o zero no mês passaria numa versão que
   *  tivesse desligado a média móvel SEMPRE — e aí o gráfico diário, que é o uso comum, perderia a
   *  suavização sem ninguém notar.
   *  ========================================================================== */
  /** ⚠️ A REGRA "SÓ POR DIA" MOROU AQUI, e agora é do servidor (AUD-XX): a série de vendas chega
   *  com `mediaFaturamento` só no agrupamento por dia, e a tela a repassa ao gráfico. */
  it('A MÉDIA MÓVEL DO GRÁFICO É A QUE VEM EM CADA PONTO', () => {
    montar();

    c.vendas.set({
      ...c.vendas()!,
      pontos: [
        { periodo: '2026-08-01', vendas: 1, faturamento: 100, concluidas: 0, valorConcluido: 0, canceladas: 0, valorCancelado: 0, mediaFaturamento: 80 },
        { periodo: '2026-08-02', vendas: 1, faturamento: 300, concluidas: 0, valorConcluido: 0, canceladas: 0, valorCancelado: 0, mediaFaturamento: null }
      ]
    });

    expect(c.serieVendas().map(p => p.media)).toEqual([80, null]);
  });

  // ============================================================ FUN-1 · o funil agrupado
  /** ===================== DUAS "PROPOSTA" NA MESMA TELA =====================
   *
   *  O gráfico desenhava as etapas dos dois funis em fila, e a tabela listava as quatro seguidas.
   *  "Proposta" aparecia duas vezes sem nada dizendo de quem era qual.
   *
   *  ⚠️ O SEGUNDO `expect` É O QUE IMPORTA. Só contar os gráficos passaria com um deles levando as
   *  quatro barras e o outro vazio: os grupos existiriam e não agrupariam nada.
   *  ========================================================================= */
  it('desenha UM gráfico por funil, com as barras daquele funil', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    expect([...raiz.querySelectorAll('.rotulo-funil')].map(h => h.textContent!.trim()))
      .toEqual(['Vendas', 'Atacado']);

    expect(c.gruposFunilEntradas().map(g => g.barras.map(b => b.rotulo))).toEqual([
      ['Novo Lead', 'Proposta'],
      ['Novo lead', 'Proposta']
    ]);
  });

  /** ===================== UMA ESCALA SÓ ENTRE OS FUNIS =====================
   *  Agrupar não pode custar a comparação. Com cada gráfico se normalizando pelo próprio maior, as
   *  16 entradas do Atacado desenhariam exatamente a mesma barra que as 42 de Vendas — e quem olha
   *  concluiria que os dois trazem o mesmo movimento.
   *
   *  ⚠️ É A DECISÃO OPOSTA À DO PAINEL, e de propósito: lá se lê FORMA e cada funil tem escala
   *  própria; aqui se lê VOLUME. Mesma tela do produto, perguntas diferentes.
   *  ====================================================================== */
  it('todos os gráficos do funil dividem a MESMA escala', () => {
    montar();

    // Escopado ao cartão do funil: a tela tem outros gráficos de barras, e contá-los todos mediria
    // outra coisa.
    const cartao = (fixture.nativeElement as HTMLElement)
      .querySelector('.rotulo-funil')!.closest('section')!;

    const graficos = [...cartao.querySelectorAll('app-grafico-barras')];
    expect(graficos.length).toBe(2);

    // ⚠️ A AFIRMAÇÃO É SOBRE A BARRA DESENHADA, não sobre o `computed`. Checar só
    //    `maximoFunilEntradas()` passava com a entrada `[escalaMaxima]` REMOVIDA do template — a
    //    conta certa existia e não chegava ao gráfico. Sabotagem feita, teste verde, defeito no ar.
    //
    //    42 e 16 são os topos dos dois funis. Sob escala única, a maior barra do Atacado tem de ser
    //    visivelmente menor que a de Vendas; com cada gráfico se normalizando, as duas encostariam
    //    no mesmo teto e a razão abaixo daria 1.
    const maiorDe = (g: Element) => Math.max(...[...g.querySelectorAll('rect.gb-barra')]
      .map(r => Number(r.getAttribute('height'))));

    const razao = maiorDe(graficos[1]) / maiorDe(graficos[0]);

    expect(razao).toBeLessThan(0.6);
    expect(razao).toBeGreaterThan(0.2);   // ~16/42, e não zero por um seletor que não achou nada
  });

  it('a tabela do funil separa as duas "Proposta" por funil', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    // ⚠️ `.tabela-funil` E NÃO `.tabela`: o seletor largo só passava porque as outras tabelas da
    // tela vinham vazias, e caiu no dia em que a lista de respostas do NPS chegou com linhas.
    const linhas = [...raiz.querySelectorAll('.tabela-funil tbody tr')]
      .map(tr => tr.classList.contains('linha-funil')
        ? `== ${tr.textContent!.trim()}`
        : tr.querySelector('td')!.textContent!.trim());

    expect(linhas).toEqual([
      '== Vendas', 'Novo Lead', 'Proposta',
      '== Atacado', 'Novo lead', 'Proposta'
    ]);
  });

  // ============================================================ FUN-1 · o filtro de etapa
  /** ===================== O SELETOR QUE TROCA A RESPOSTA =====================
   *
   *  A etapa escolhida recorta o relatório INTEIRO. Numa lista chata, as duas "Proposta" são
   *  indistinguíveis, e pegar a errada devolve os números do outro processo — sem erro, sem aviso,
   *  e plausíveis demais para alguém conferir.
   *
   *  ⚠️ A AFIRMAÇÃO FORTE É A SEGUNDA. Checar só os rótulos dos grupos passaria com as cinco
   *  etapas despejadas dentro do primeiro `<optgroup>`: o grupo certo existiria e não agruparia
   *  nada. O que prova o conserto é cada "Proposta" estar DEBAIXO do seu próprio funil.
   *  ========================================================================= */
  it('agrupa o filtro de etapa por funil, cada "Proposta" debaixo do seu', () => {
    montar();
    abrirMaisFiltros();
    const raiz = fixture.nativeElement as HTMLElement;

    const campo = [...raiz.querySelectorAll('label.campo')]
      .find(l => l.querySelector('span')?.textContent?.trim() === 'Etapa')!;
    const seletor = campo.querySelector('select')!;

    expect([...seletor.querySelectorAll('optgroup')].map(g => g.label))
      .toEqual(['Vendas', 'Atacado']);

    const propostas = [...seletor.querySelectorAll('option')]
      .filter(o => o.textContent!.trim() === 'Proposta');

    expect(propostas.length).toBe(2);
    expect(propostas.map(o => (o.parentElement as HTMLOptGroupElement).label))
      .toEqual(['Vendas', 'Atacado']);
  });

  // ============================================================ o rótulo
  /** ===================== O QUE O PROMPT PROÍBE =====================
   *  "Não rotule como 'no período' um dado que é foto atual."
   *
   *  As duas coisas aparecem na mesma seção, e por isso a separação precisa estar VISÍVEL: dois
   *  títulos e duas colunas com nomes diferentes. Um teste que só checasse os números passaria
   *  com os dois rótulos trocados.
   *  ============================================================== */
  it('separa "entradas no período" de "situação agora", com títulos e colunas distintos', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const titulos = [...raiz.querySelectorAll('.sub-titulo')].map(t => t.textContent!.trim());
    expect(titulos).toContain('Entradas no período');
    expect(titulos).toContain('Situação agora');

    const cabecalhos = [...raiz.querySelectorAll('table th')].map(t => t.textContent!.trim());
    expect(cabecalhos).toContain('Entradas no período');
    expect(cabecalhos).toContain('Contatos agora');

    // E os NÚMEROS não se confundem: 42 entrou em "Novo Lead", 40 está lá agora — os dois na
    // mesma linha que veio do servidor (AUD-XX), em colunas separadas.
    const novoLead = [...raiz.querySelectorAll('.tabela-funil tbody tr')]
      .find(tr => tr.textContent!.includes('Novo Lead'))!;
    const celulas = [...novoLead.querySelectorAll('td')].map(td => td.textContent!.trim());
    expect(celulas[1]).toBe('42');
    expect(celulas[2]).toBe('40');
    expect(c.gruposFunilEntradas()[0].barras[0].valor).toBe(42);
  });

  /** A trilha só existe desde o deploy do AUD-1. Sem esta frase na tela, um cliente de um ano vê
   *  zero entradas e conclui que o relatório está quebrado. */
  it('avisa desde quando a movimentação é registrada', () => {
    montar();
    const texto = (fixture.nativeElement as HTMLElement)
      .querySelector('.aviso-trilha')!.textContent!;

    expect(texto).toContain('07/08/2026');
    expect(texto).toContain('não porque nada aconteceu');
  });

  it('sem trilha nenhuma, explica em vez de mostrar zero seco', () => {
    montar('dono', OPCOES, { ...FUNIL, trilhaComecaEm: null });
    const texto = (fixture.nativeElement as HTMLElement)
      .querySelector('.aviso-trilha')!.textContent!;

    expect(texto).toContain('Ainda não há movimentação registrada');
  });

  // ============================================================ cancelado
  /** Cancelado fica FORA do total e aparece assim mesmo — faturamento que some sem rastro é pior
   *  que faturamento errado. O riscado diz na forma o que o rótulo diz em texto. */
  it('mostra o cancelado à parte do faturamento, e riscado', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    const rotulos = [...raiz.querySelectorAll('.kpi-rotulo')].map(r => r.textContent!.trim());
    expect(rotulos).toContain('Cancelado (fora do total)');

    const riscado = raiz.querySelector('.kpi-linha .riscado')!;
    expect(riscado.textContent).toContain('300');

    // O faturamento NÃO desconta a cancelada — ela já saiu no servidor.
    expect(c.vendas()!.totais.faturamento).toBe(1000);
  });

  // ============================================================ exportação
  /** ===================== O CSV VEM DO SERVIDOR =====================
   *  "Para volumes grandes, gere no servidor e sirva por endpoint. Não monte CSV de dez mil
   *  linhas no browser."
   *
   *  O teste clica no botão de verdade e confere que saiu UMA requisição, para a rota de CSV, com
   *  `responseType: 'blob'` — o BOM UTF-8 é byte, e lê-lo como texto o transformaria num
   *  caractere invisível no meio do primeiro cabeçalho.
   *  ============================================================== */
  it('UMA EXPORTAÇÃO SÓ, e ela manda o relatório escolhido', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    // ⚠️ UM, e é metade do teste. Eram sete botões iguais espalhados pelos cartões; se algum tiver
    //    ficado para trás, o `querySelectorAll` acusa aqui.
    const botoes = [...raiz.querySelectorAll<HTMLButtonElement>('button')]
      .filter(b => b.textContent!.includes('Exportar CSV'));
    expect(botoes.length).toBe(1);

    // O seletor manda no que é exportado — não a posição do botão na página.
    c.exportarQual.set('perdas');
    fixture.detectChanges();
    botoes[0].click();

    const req = http.expectOne(r => r.url.includes('/relatorios/') && r.url.endsWith('/csv'));
    expect(req.request.url).toContain('/relatorios/perdas/csv');
    expect(req.request.method).toBe('GET');
    expect(req.request.responseType).toBe('blob');
    // Os filtros da barra vão junto: exportar o período errado é pior que não exportar.
    expect(req.request.params.get('de')).toBe(c.de());
    expect(req.request.params.get('ate')).toBe(c.ate());

    req.flush(new Blob(['x'], { type: 'text/csv' }));
  });

  // ============================================================ papel
  it('vendedor recebe uma opção só de responsável, e o seletor nasce travado', async () => {
    montar('vendedor', { ...OPCOES, responsaveis: [{ id: 2, nome: 'Bruno' }] });

    // ⚠️ O `await` NÃO é enfeite. `NgModel` faz a própria configuração dentro de um
    // `Promise.resolve().then(...)`, e é lá que o `[disabled]` chega ao elemento. Sem soltar o
    // microtask, o teste lê o estado de antes e falha com a tela correta.
    abrirMaisFiltros();
    await Promise.resolve();
    fixture.detectChanges();

    // Ancorado por id, não por posição na grade: reordenar a barra não pode fazer um teste de
    // permissão passar a medir o seletor de origem.
    const select = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLSelectElement>('#f-responsavel')!;

    expect(c.opcoes().responsaveis.length).toBe(1);
    expect(select.disabled).toBeTrue();
  });

  it('dono escolhe entre os responsáveis da equipe', async () => {
    montar('dono');
    abrirMaisFiltros();
    await Promise.resolve();
    fixture.detectChanges();

    const select = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLSelectElement>('#f-responsavel')!;

    expect(select.disabled).toBeFalse();
    expect(select.querySelectorAll('option').length).toBe(3);   // Todos + Ana + Bruno
  });

  // ============================================================ período
  /** ⚠️ VOLTAR PARA "TODOS" FILTRAVA PELO RESPONSÁVEL 0 — achado em revisão.
   *
   *  O `<select>` usa `[ngValue]`, e com ele o evento já chega TIPADO: `null` para "Todos". O
   *  `(ngModelChange)` fazia `$event === 'null' ? null : +$event` — e `+null` é 0. Escolher Ana e
   *  voltar para Todos deixava o relatório pedindo o responsável de id 0, e ele voltava vazio.
   *
   *  O mesmo padrão estava em quatro telas; o filtro de etiqueta da caixa já tinha sido consertado
   *  antes, o que prova que alguém tropeçou nisso e só aquele ficou certo. */
  it('VOLTAR PARA "TODOS" NÃO FILTRA PELO RESPONSÁVEL 0', async () => {
    montar('dono');
    abrirMaisFiltros();
    await Promise.resolve();
    fixture.detectChanges();

    const select = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLSelectElement>('#f-responsavel')!;

    const escolher = (indice: number) => {
      select.value = select.options[indice].value;
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };

    escolher(1);
    expect(c.responsavelId()).toBe(1);

    escolher(0);
    expect(c.responsavelId()).withContext('a opção "Todos" virou um id').toBeNull();

    // O que a troca tiver pedido ao servidor é respondido — o `afterEach` confere.
    for (const r of http.match(() => true)) {
      const url = r.request.url;
      if (url.endsWith('/vendas')) r.flush(VENDAS);
      else if (url.endsWith('/funil')) r.flush(FUNIL);
      else if (url.endsWith('/nps')) r.flush(NPS_FIXTURE);
      else if (url.endsWith('/respostas')) r.flush(RESPOSTAS);
      else if (url.endsWith('/recorrentes')) r.flush({ totalCount: 0, pagina: 1, tamanhoPagina: 20, totalPaginas: 1, itens: [] });
      else r.flush([]);
    }
  });

  it('período invertido é recusado na tela, sem ida ao servidor', () => {
    montar();
    c.de.set('2026-08-30');
    c.ate.set('2026-08-01');
    c.carregar();

    expect(c.erro()).toContain('não pode ser depois');
    http.expectNone(() => true);
  });

  it('o atalho "mês anterior" cobre o mês inteiro, não até hoje', () => {
    montar();
    c.aplicarAtalho('mes-anterior');

    const de = new Date(c.de() + 'T00:00:00');
    const ate = new Date(c.ate() + 'T00:00:00');

    expect(de.getDate()).withContext('começa no dia 1').toBe(1);
    // Somar um dia ao fim tem que virar o mês: é assim que se prova "último dia" sem repetir a
    // tabela de meses dentro do teste.
    const seguinte = new Date(ate);
    seguinte.setDate(seguinte.getDate() + 1);
    expect(seguinte.getDate()).withContext('termina no último dia do mês').toBe(1);

    for (const r of http.match(() => true)) r.flush({});
  });

  // ============================================================ NPS-1 · a pesquisa pós-venda
  function secaoNps(): HTMLElement {
    return (fixture.nativeElement as HTMLElement).querySelector('.bloco-nps') as HTMLElement;
  }

  /** O NPS com SINAL: "+25". Sem o "+", um NPS positivo lê como contagem — "25" o quê? */
  it('A PESQUISA MOSTRA O NPS COM SINAL, A TAXA E QUEM AINDA PODE RESPONDER', () => {
    montar();
    const s = secaoNps();

    expect(s.querySelector('[data-teste="nps"]')!.textContent!.trim()).toBe('+25');
    expect(s.textContent).toContain('4 de 7');
    expect(s.textContent).toContain('57,1%');
    expect(s.textContent).toContain('2 · 1 · 1');

    // ⚠️ A FRASE QUE SALVA A LEITURA DA TAXA: sem ela, 57% numa semana corrente parece pouco.
    expect(s.querySelector('[data-teste="abertas"]')!.textContent).toContain('1 ainda pode responder');
  });

  /** ⚠️ ZERO É UM NPS REAL. "Sem respostas" é outra coisa, e a tela não pode escrever 0 para ela. */
  it('SEM RESPOSTA O NPS É "sem respostas", E NÃO ZERO', () => {
    montar('dono', OPCOES, FUNIL, {
      ...NPS_FIXTURE,
      totais: { ...NPS_FIXTURE.totais, respondidas: 0, promotores: 0, neutros: 0, detratores: 0, nps: null, taxaDeResposta: 0 },
      comparativo: null
    });

    expect(secaoNps().querySelector('[data-teste="nps"]')!.textContent!.trim()).toBe('sem respostas');
  });

  it('NPS NEGATIVO SAI COM O SINAL DE MENOS TIPOGRÁFICO, E ZERO SAI SEM SINAL', () => {
    montar();
    expect(c.textoNps(-12.5)).toBe('−12,5');
    expect(c.textoNps(0)).toBe('0');
    expect(c.textoNps(100)).toBe('+100');
  });

  /** Sem pesquisa enviada, a seção diz ONDE ligar — e não desenha quatro KPIs zerados e um
   *  gráfico vazio, que pareceriam uma pesquisa ligada que ninguém responde. */
  it('NENHUMA PESQUISA ENVIADA: A SEÇÃO APONTA PARA AS CONFIGURAÇÕES', () => {
    montar('dono', OPCOES, FUNIL, {
      totais: {
        enviadas: 0, respondidas: 0, expiradas: 0, canceladas: 0, aindaAbertas: 0,
        promotores: 0, neutros: 0, detratores: 0, nps: null, taxaDeResposta: null
      },
      distribuicao: Array.from({ length: 11 }, (_, nota) => ({ nota, quantas: 0 })),
      comparativo: null
    });

    const s = secaoNps();
    expect(s.textContent).toContain('Nenhuma pesquisa saiu no período');
    expect(s.querySelector('[data-teste="nps"]')).toBeNull();
    expect(s.querySelector('app-grafico-barras')).toBeNull();
  });

  /** As onze barras, de 0 a 10, na ordem do servidor — o rótulo É a nota. */
  it('A DISTRIBUIÇÃO DESENHA AS ONZE NOTAS NA ORDEM', () => {
    montar();

    const barras = c.barrasNps();
    expect(barras.map(b => b.rotulo)).toEqual(['0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '10']);
    expect(barras[10].valor).toBe(2);
  });

  it('A PESQUISA ENTRA NA LISTA DE EXPORTAÇÃO E BUSCA O CSV DO SERVIDOR', () => {
    montar();

    expect(c.relatoriosExportaveis.map(r => r.id)).toContain('nps');

    c.exportar('nps');
    const req = http.expectOne(r => r.url.endsWith('/relatorios/nps/csv'));
    req.flush(new Blob(['x']));
  });

  // ============================================================ NPS-1 · a lista de respostas
  function secaoRespostas(): HTMLElement {
    return (fixture.nativeElement as HTMLElement).querySelector('.bloco-respostas') as HTMLElement;
  }

  /** "Comprou de novo" com a DATA quando sim, e "Não" quando não. Sem a data, o vendedor não sabe
   *  se a volta foi ontem ou há seis meses. */
  it('A LISTA MOSTRA QUEM VOLTOU COM A DATA, E "NÃO" PARA QUEM NÃO VOLTOU', () => {
    montar();
    const linhas = [...secaoRespostas().querySelectorAll('tbody tr')];

    expect(linhas.length).toBe(2);
    expect(linhas[0].textContent).toContain('Maria');
    expect(linhas[0].querySelector('[data-teste="comprou"]')!.textContent!.trim()).toBe('Sim · 15/09/2026');
    expect(linhas[1].querySelector('[data-teste="comprou"]')!.textContent!.trim()).toBe('Não');

    // Sem responsável e sem comentário são ditos, não deixados em branco.
    expect(linhas[1].textContent).toContain('Sem responsável');
    expect(linhas[1].textContent).toContain('—');
  });

  /** O atalho vai com o NOME DO ENUM do servidor — é ele que decide o que o atalho significa. */
  it('O ATALHO VAI AO SERVIDOR COM O NOME DELE E O PRAZO EM DIAS', () => {
    montar();

    (secaoRespostas().querySelector('[data-teste="atalho-promotores"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const req = http.expectOne(r => r.url.endsWith('/relatorios/nps/respostas'));
    expect(req.request.params.get('atalho')).toBe('PromotoresQueNaoVoltaram');
    expect(req.request.params.get('diasSemCompra')).toBe('60');
    req.flush(RESPOSTAS);
    fixture.detectChanges();

    // E o que o atalho quer dizer fica escrito embaixo dele.
    expect(secaoRespostas().textContent).toContain('nenhuma compra há');
  });

  /** ⚠️ "TANTO FAZ" NÃO VAI NA REQUISIÇÃO. Mandar `false` no lugar do nulo esconderia todo mundo
   *  que voltou — metade da lista sumindo por um filtro que ninguém escolheu. */
  it('COMPROU DE NOVO "TANTO FAZ" NÃO VAI NA REQUISIÇÃO, E "NÃO" VAI COMO false', () => {
    montar();

    c.paginaRespostas(1);
    const semFiltro = http.expectOne(r => r.url.endsWith('/relatorios/nps/respostas'));
    expect(semFiltro.request.params.has('comprouDeNovo')).toBeFalse();
    expect(semFiltro.request.params.has('faixa')).toBeFalse();
    semFiltro.flush(RESPOSTAS);

    c.comprouDeNovo.set(false);
    c.faixaRespostas.set('promotor');
    c.paginaRespostas(1);
    const comFiltro = http.expectOne(r => r.url.endsWith('/relatorios/nps/respostas'));
    expect(comFiltro.request.params.get('comprouDeNovo')).toBe('false');
    expect(comFiltro.request.params.get('faixa')).toBe('promotor');
    comFiltro.flush(RESPOSTAS);
  });

  /** O vazio de um atalho é uma BOA notícia, e a frase diz isso. "Nenhuma resposta" ali faria o
   *  dono achar que a pesquisa parou. */
  it('O VAZIO DE "DETRATORES SEM RETORNO" DIZ QUE TODOS FORAM ATENDIDOS', () => {
    montar();

    c.escolherAtalho('DetratoresSemRetorno');
    http.expectOne(r => r.url.endsWith('/relatorios/nps/respostas'))
      .flush({ totalCount: 0, pagina: 1, tamanhoPagina: 20, totalPaginas: 1, itens: [] });
    fixture.detectChanges();

    expect(secaoRespostas().querySelector('[data-teste="sem-respostas"]')!.textContent)
      .toContain('Todo detrator já recebeu uma mensagem da equipe');
  });

  /** "Página X de Y" é o do SERVIDOR (AUD-XX, #21). Os números são de propósito impossíveis para
   *  o tamanho da página — se a tela voltar a dividir o total, o teste mostra outra conta. */
  it('AS PÁGINAS DOS CLIENTES RECORRENTES E DAS RESPOSTAS SÃO AS DO SERVIDOR', () => {
    montar();

    c.paginaRecorrentes(2);
    // 45 clientes de 20 em 20 seriam 3 páginas; o servidor diz 9.
    http.expectOne(r => r.url.endsWith('/recorrentes'))
      .flush({ itens: [], totalCount: 45, pagina: 2, tamanhoPagina: 20, totalPaginas: 9 });

    c.paginaRespostas(3);
    http.expectOne(r => r.url.endsWith('/respostas'))
      .flush({ itens: [], totalCount: 45, pagina: 3, tamanhoPagina: 20, totalPaginas: 8 });

    expect(c.totalPaginasRecorrentes()).toBe(9);
    expect(c.recorrentesPagina()).toBe(2);
    expect(c.totalPaginasRespostas()).toBe(8);
    expect(c.respostasPagina()).toBe(3);
  });
});
