import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { RealtimeFalso, rotaFalsa } from '../telas-do-painel';
import { Contatos } from './contatos';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';

/** A LISTA DE CONTATOS, e especificamente o filtro por etapa.
 *
 *  ===================== POR QUE ESTA SUÍTE NASCEU =====================
 *  A tela não tinha spec nenhuma — só entrava nas suítes genéricas de render e largura, que
 *  provam que ela DESENHA, não que ela funciona.
 *
 *  E ela tinha um defeito que viveu blocos inteiros: `funil.quadro(1)`, escrito quando o primeiro
 *  parâmetro era `porColuna` e `1` queria dizer "um card por coluna". Quando `pipeline` entrou na
 *  FRENTE da assinatura, a chamada continuou compilando e passou a pedir a pipeline de id 1.
 *
 *  Funcionava por acidente na PRIMEIRA empresa, cuja pipeline é justamente a de id 1. Em qualquer
 *  outra o filtro ficava vazio — sem erro, sem log, e sem como filtrar por etapa.
 *  ==================================================================== */
describe('contatos — o filtro por etapa', () => {
  let http: HttpTestingController;

  const FUNIS = [
    { id: 7, nome: 'Vendas', cor: '#1E4028', ordem: 1, padrao: true, etapas: 2, contatos: 3 },
    { id: 9, nome: 'Pós-venda', cor: '#7FA88B', ordem: 2, padrao: false, etapas: 1, contatos: 0 }
  ];

  const PAGINA_VAZIA = {
    total: 0, numeroPagina: 1, tamanho: 30, itens: [],
    contagens: { abertos: 0, ganhos: 0, perdidos: 0, todos: 0 }
  };

  function coluna(etapaId: number, nome: string) {
    return {
      etapaId, nome, ordem: 1, cor: '#7FA88B', eGanho: false,
      total: 0, valorTotal: 0, concluidas: 0, contatos: [], temMais: false
    };
  }

  function montar(papel: 'dono' | 'gestor' | 'vendedor' = 'vendedor') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        { provide: ActivatedRoute, useValue: rotaFalsa() }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      // VENDEDOR de propósito: o dono dispara `/equipe` a mais, e este teste é sobre o filtro.
      usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel, permissoes: PERMISSOES_DE[papel], empresaNome: 'X' }
    } as never);

    const fixture = TestBed.createComponent(Contatos);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => TestBed.resetTestingModule());

  it('PEDE AS ETAPAS DE TODOS OS FUNIS, E NÃO DE UM FIXO', () => {
    const fixture = montar();

    // A lista de contatos em si não interessa aqui.
    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush({ itens: [], temMais: false, total: 0 }));

    // ⚠️ Nenhum dos dois funis tem id 1: com 1 na lista, o defeito antigo passaria.
    http.expectOne(r => r.url.endsWith('/pipelines')).flush(FUNIS);
    fixture.detectChanges();

    const quadros = http.match(r => r.url.includes('/funil'));
    expect(quadros.length).withContext('um por funil').toBe(2);

    const urls = quadros.map(q => q.request.urlWithParams).sort();
    expect(urls[0]).toContain('pipeline=7');
    expect(urls[1]).toContain('pipeline=9');

    // E UM card por coluna: o filtro quer os NOMES das etapas, não o quadro inteiro.
    expect(urls[0]).toContain('porColuna=1');
    expect(urls[1]).toContain('porColuna=1');

    quadros[0].flush({ colunas: [coluna(10, 'Novo Lead'), coluna(11, 'Venda')] });
    quadros[1].flush({ colunas: [coluna(20, 'Entrada')] });
    fixture.detectChanges();

    // As etapas chegam AGRUPADAS por funil: "Proposta" de Vendas e "Proposta" de Pós-venda são
    // etapas diferentes, e sem o grupo o dono escolheria a errada sem perceber.
    const grupos = [...(fixture.nativeElement as HTMLElement).querySelectorAll('optgroup')];
    expect(grupos.map(g => g.label)).toEqual(['Vendas', 'Pós-venda']);
    expect(grupos[0].querySelectorAll('option').length).toBe(2);
    expect(grupos[1].querySelectorAll('option').length).toBe(1);
  });

  /** ⚠️ O SELO É O QUE O SERVIDOR MANDA — A TELA NÃO RECALCULA.
   *
   *  A regra morava aqui, e divergiu das abas: `ganhoEm` perguntado antes de "tem negócio vivo?"
   *  punha a Ysia — três negócios abertos e uma compra antiga — como "venda fechada" DENTRO da aba
   *  "Em aberto". Agora ela é do servidor (`RegrasNegociacao.Situacao`, testada lá contra as abas).
   *
   *  As linhas abaixo são CONTRADITÓRIAS de propósito: todas com negócio aberto e `ganhoEm`
   *  carimbado. Se a tela voltar a olhar para esses campos, os quatro selos viram um só. */
  it('O SELO DA LINHA É A SITUAÇÃO QUE O SERVIDOR MANDA', () => {
    const fixture = montar();

    const linha = (id: number, situacao: string) => ({
      id, nome: `Pessoa ${id}`, telefone: '5584900000000', email: null, origem: 'whatsapp',
      etapaId: 28, etapaNome: 'Separado', ordemKanban: 1000,
      responsavelId: null, responsavelNome: null, valor: null,
      ganhoEm: '2026-08-01T10:00:00Z', perdidoEm: '2026-07-01T10:00:00Z', situacao,
      criadoEm: '2026-08-01T10:00:00Z', conversaId: null, aguardandoDesde: null, naoLidas: 0,
      negocios: [{ id: id * 10, pipelineId: 3, pipelineNome: 'Vendas', etapaId: 28,
                   etapaNome: 'Separado', status: 'aberta', valor: null }]
    });

    http.expectOne(r => r.url.includes('/contatos')).flush({
      total: 4, numeroPagina: 1, tamanho: 30,
      contagens: { abertos: 2, ganhos: 1, perdidos: 1, todos: 4 },
      itens: [linha(1, 'sem_negocio'), linha(2, 'aberto'), linha(3, 'ganho'), linha(4, 'perdido')]
    });
    fixture.detectChanges();

    const selos = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody .selo')]
      .filter(s => !s.closest('.etapas'))
      .map(s => s.textContent!.trim());

    expect(selos).toEqual(['sem negócio', 'em aberto', 'venda fechada', 'perdido']);
  });

  /** ⚠️ A COLUNA "ETAPA" ESCOLHIA UMA DAS TRES E NAO DIZIA DE QUAL FUNIL.
   *
   *  Relatado assim: "por que na lista de contato Ysia ficou com a etiqueta de impedimento? esse
   *  contato esta em 3 funil diferente com etiquetas diferentes". "Impedimento" era uma ETAPA, do
   *  funil Teste, e venceu a disputa por ter o id mais alto entre as abertas. */
  it('A COLUNA DE ETAPAS MOSTRA TODOS OS FUNIS, COM O NOME DE CADA UM', () => {
    const fixture = montar();

    http.expectOne(r => r.url.includes('/contatos')).flush({
      total: 1, numeroPagina: 1, tamanho: 30,
      contagens: { abertos: 1, ganhos: 0, perdidos: 0, todos: 1 },
      itens: [{
        id: 1002, nome: 'Ysia', telefone: '5584900000000', email: null, origem: 'whatsapp',
        etapaId: 42, etapaNome: 'Impedimento', ordemKanban: 1000,
        responsavelId: null, responsavelNome: null, valor: null,
        ganhoEm: '2026-09-17T10:00:00Z', perdidoEm: null, situacao: 'aberto',
        criadoEm: '2026-08-01T10:00:00Z',
        conversaId: null, aguardandoDesde: null, naoLidas: 0,
        negocios: [
          { id: 2262, pipelineId: 3, pipelineNome: 'Vendas', etapaId: 28, etapaNome: 'Separado', status: 'aberta', valor: null },
          { id: 2263, pipelineId: 4, pipelineNome: 'Pós-venda', etapaId: 35, etapaNome: 'A Caminho', status: 'aberta', valor: null },
          { id: 2265, pipelineId: 6, pipelineNome: 'Teste', etapaId: 42, etapaNome: 'Impedimento', status: 'aberta', valor: null }
        ]
      }]
    });
    fixture.detectChanges();

    const chips = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.etapas .selo')]
      .map(s => s.textContent!.replace(/\s+/g, ' ').trim());

    // Os TRES, cada um dizendo o funil — e na ordem do menu, nao pelo id.
    expect(chips).toEqual([
      'Vendas · Separado', 'Pós-venda · A Caminho', 'Teste · Impedimento'
    ]);

    // ⚠️ E o selo de situacao NAO diz "venda fechada", mesmo com `ganhoEm` carimbado: ela tem
    // tres negocios vivos, e e o que a aba "Em aberto" ja dizia.
    // Pelo seletor, e nao por indice de coluna: a coluna de VALOR so aparece quando alguem da
    // pagina tem valor, entao contar `td` daria uma posicao diferente conforme a fixture.
    const situacao = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody .selo')]
      .filter(s => !s.closest('.etapas'))
      .map(s => s.textContent!.trim());

    expect(situacao).toEqual(['em aberto']);
  });

  /** ===================== O CONTATO QUE SUMIU DA LISTA =====================
   *  Relatado assim: "fechei os cards da Ysia em todos os funis e o contato dele sumiu da lista
   *  de contato". Ela não tinha sumido — a tela abria em "Em aberto", e quem fecha todos os
   *  negócios sai dessa aba. Estava em "Ganhos", uma aba ao lado, sem nada apontando para lá.
   *
   *  Duas coisas consertam isso, e as duas estão aqui: a tela abre no diretório inteiro, e cada
   *  aba diz quantos tem.
   *  ======================================================================== */
  it('ABRE EM "TODOS", E CADA ABA DIZ QUANTOS TEM', () => {
    const fixture = montar();

    const pedido = http.expectOne(r => r.url.includes('/contatos'));

    // ⚠️ `filtro=Todos` NA PRIMEIRA CHAMADA. Era `Abertos`, e é o que escondia o cliente.
    expect(pedido.request.params.get('filtro')).toBe('Todos');

    pedido.flush({
      total: 15, numeroPagina: 1, tamanho: 30, itens: [],
      contagens: { abertos: 13, ganhos: 2, perdidos: 0, todos: 15 }
    });
    fixture.detectChanges();

    const abas = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.aba')]
      .map(a => a.textContent!.replace(/\s+/g, ' ').trim());

    // O zero de "Perdidos" APARECE: esconder deixaria a aba parecendo não-carregada, e zero é
    // uma resposta — "não há ninguém ali".
    expect(abas).toEqual(['Em aberto 13', 'Ganhos 2', 'Perdidos 0', 'Todos 15']);
  });

  /** ⚠️ O ESTADO VAZIO MENTIA SOBRE UMA BASE CHEIA.
   *
   *  `temFiltro()` era `filtro() !== 'Abertos'`, escrito como atalho para "o usuário escolheu
   *  algo". Numa base em que todos já compraram, a aba padrão ficava vazia e a tela dizia
   *  "Nenhum contato ainda. Cadastre um contato ou aguarde alguém mandar mensagem no WhatsApp" —
   *  mandando cadastrar gente para quem já tinha a base inteira. */
  it('ABA VAZIA NÃO DIZ QUE A BASE ESTÁ VAZIA', () => {
    const fixture = montar();
    const c = fixture.componentInstance;

    http.expectOne(r => r.url.includes('/contatos')).flush({
      total: 15, numeroPagina: 1, tamanho: 30, itens: [],
      contagens: { abertos: 0, ganhos: 15, perdidos: 0, todos: 15 }
    });
    fixture.detectChanges();

    // Numa aba de estado, o vazio é do RECORTE — e o texto manda voltar para "Todos".
    c.trocarFiltro('Abertos');
    http.expectOne(r => r.url.includes('/contatos')).flush({
      total: 0, numeroPagina: 1, tamanho: 30, itens: [],
      contagens: { abertos: 0, ganhos: 15, perdidos: 0, todos: 15 }
    });
    fixture.detectChanges();

    const vazio = () =>
      (fixture.nativeElement as HTMLElement).querySelector('.vazio')!.textContent!;

    expect(c.temFiltro()).toBeTrue();
    expect(vazio()).toContain('Nenhum contato com esses filtros');
    expect(vazio()).not.toContain('Nenhum contato ainda');

    // E em "Todos" vazio — a ÚNICA leitura em que a base está mesmo vazia — a frase volta.
    c.trocarFiltro('Todos');
    http.expectOne(r => r.url.includes('/contatos')).flush({
      total: 0, numeroPagina: 1, tamanho: 30, itens: [],
      contagens: { abertos: 0, ganhos: 0, perdidos: 0, todos: 0 }
    });
    fixture.detectChanges();

    expect(c.temFiltro()).toBeFalse();
    expect(vazio()).toContain('Nenhum contato ainda');
  });

  // ==================================================================== importar (issue #8)
  /** ⚠️ QUEM VÊ O BOTÃO É QUEM O SERVIDOR DEIXA IMPORTAR, e a primeira versão errou isso: estava
   *  `ehDono`, enquanto o serviço aceita dono OU gestor. O gestor ficava sem o botão de uma
   *  operação permitida a ele.
   *
   *  É o espelho do "botão que sempre erra", e o lado pior: oferecer o que será recusado a pessoa
   *  descobre no clique; esconder o que seria aceito ela nunca descobre — some do produto sem
   *  deixar rastro.
   *
   *  ⚠️ HOJE É UM LINK para `/importar`: o modal de dois passos foi absorvido pela tela nova
   *  (INT-XX), onde o dono diz o que cada coluna do arquivo significa. */
  it('O BOTÃO DE IMPORTAR SEGUE O MESMO CORTE DO SERVIDOR', () => {
    for (const [papel, esperado] of [
      ['dono', true], ['gestor', true], ['vendedor', false]
    ] as const) {
      const fixture = montar(papel);
      http.match(r => r.url.includes('/contatos')).forEach(r => r.flush(PAGINA_VAZIA));
      fixture.detectChanges();

      const topo = (fixture.nativeElement as HTMLElement).querySelector('.topo')!;
      const importar = [...topo.querySelectorAll('a, button')]
        .find(x => x.textContent!.trim() === 'Importar');

      expect(!!importar)
        .withContext(`${papel} ${esperado ? 'deveria ver' : 'não deveria ver'} "Importar"`)
        .toBe(esperado);

      if (esperado) {
        expect(importar!.getAttribute('href'))
          .withContext('o botão tem de levar para a tela de importar').toContain('/importar');
      }

      TestBed.resetTestingModule();
    }
  });

  /** ⚠️ "LIMPAR" VOLTA PARA "TODOS", que é o padrão da tela — achado em revisão. Ele voltava para
   *  "Em aberto", que ficou para trás quando o padrão mudou: `temFiltro()` continuava verdadeiro e
   *  o estado vazio mandava a pessoa voltar para "Todos" — o que o botão deveria ter feito. */
  it('LIMPAR FILTROS VOLTA PARA "TODOS", E NÃO DEIXA RECORTE LIGADO', () => {
    const fixture = montar();
    const c = fixture.componentInstance;
    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush(PAGINA_VAZIA));

    c.trocarFiltro('Ganhos');
    http.expectOne(r => r.url.includes('/contatos')).flush(PAGINA_VAZIA);
    c.busca.set('maria');

    c.limparFiltros();

    const pedido = http.expectOne(r => r.url.includes('/contatos'));
    expect(pedido.request.params.get('filtro')).toBe('Todos');
    expect(c.temFiltro()).withContext('"Limpar" deixou um recorte ligado').toBeFalse();
    pedido.flush(PAGINA_VAZIA);
  });

  it('FUNIL SEM ETAPA NÃO VIRA GRUPO VAZIO NO SELETOR', () => {
    // Um `<optgroup>` sem opção aparece como um rótulo morto que não dá para escolher.
    const fixture = montar();

    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush({ itens: [], temMais: false, total: 0 }));
    http.expectOne(r => r.url.endsWith('/pipelines')).flush(FUNIS);
    fixture.detectChanges();

    const quadros = http.match(r => r.url.includes('/funil'));
    quadros[0].flush({ colunas: [coluna(10, 'Novo Lead')] });
    quadros[1].flush({ colunas: [] });
    fixture.detectChanges();

    const grupos = [...(fixture.nativeElement as HTMLElement).querySelectorAll('optgroup')];
    expect(grupos.map(g => g.label)).toEqual(['Vendas']);
  });
});
