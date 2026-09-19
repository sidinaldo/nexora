import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { RealtimeFalso, rotaFalsa } from '../telas-do-painel';
import { Contatos } from './contatos';

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

  const PREVIA = {
    total: 3, novas: 2, repetidas: 1, invalidas: 0,
    amostra: [
      { linha: 2, nome: 'Maria', telefone: '5584988887777', email: null, origem: null,
        observacoes: null, situacao: 'nova', motivo: null },
      { linha: 3, nome: 'Repetida', telefone: '5584999996666', email: null, origem: null,
        observacoes: null, situacao: 'repetida', motivo: 'Já existe um contato com este telefone.' }
    ]
  };

  /** O `change` do `<input type="file">`, sem tocar em disco. O componente só lê
   *  `target.files[0]`, então um `DataTransfer` monta o evento inteiro. */
  function eventoComArquivo(nome: string): Event {
    const dt = new DataTransfer();
    dt.items.add(new File(['nome;telefone\nMaria;84988887777'], nome, { type: 'text/csv' }));

    const input = document.createElement('input');
    input.type = 'file';
    input.files = dt.files;

    return { target: input } as unknown as Event;
  }

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
      usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel, empresaNome: 'X' }
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

  /** ⚠️ O LEAD DA CAIXA NAO TEM ETAPA (E6), e a tela dizia "em aberto" sobre ele — o mesmo
   *  rotulo de quem tem negocio aberto. Eram tres estados onde ha quatro.
   *
   *  ⚠️ E DEPOIS A ORDEM DAS PERGUNTAS VIROU DEFEITO SOZINHA. `ganhoEm` era consultado antes de
   *  "tem negocio vivo?", e ele e o MAXIMO de todas as vendas nao canceladas — uma compra de
   *  meses atras basta. Quem comprou e voltou a negociar aparecia como "venda fechada" com tres
   *  negocios abertos, e na MESMA tela a aba "Em aberto" o listava: as duas metades discordando
   *  sobre a mesma pessoa, uma ao lado da outra. */
  it('SITUAÇÃO: ter negócio vivo manda sobre já ter vendido', () => {
    const fixture = montar();
    const c = fixture.componentInstance;

    const negocio = (status: 'aberta' | 'ganha', pipelineId = 1) => ({
      id: pipelineId * 10, pipelineId, pipelineNome: 'F', etapaId: 1, etapaNome: 'E', status,
      valor: null
    });

    // Nunca teve negocio: o lead que chegou pela caixa.
    expect(c.situacao({ negocios: [], ganhoEm: null, perdidoEm: null } as never))
      .toBe('sem-negocio');

    // ⚠️ O CASO DA YSIA: comprou antes (`ganhoEm` carimbado) e tem tres negocios abertos hoje.
    // Esta e a linha que dizia "venda fechada".
    expect(c.situacao({
      negocios: [negocio('aberta', 1), negocio('aberta', 2), negocio('aberta', 3)],
      ganhoEm: '2026-08-01', perdidoEm: null
    } as never)).toBe('aberto');

    // Pedido a caminho e SO isso: nada aberto, uma ganha esperando conclusao.
    expect(c.situacao({ negocios: [negocio('ganha')], ganhoEm: '2026-08-01', perdidoEm: null } as never))
      .toBe('ganho');

    // Vendeu e concluiu: nada vivo, mas o carimbo fica.
    expect(c.situacao({ negocios: [], ganhoEm: '2026-08-01', perdidoEm: null } as never))
      .toBe('ganho');

    // So perda: `negocios` vem vazia (perdida nao esta no quadro), e o carimbo e quem responde.
    expect(c.situacao({ negocios: [], ganhoEm: null, perdidoEm: '2026-08-01' } as never))
      .toBe('perdido');

    // E um negocio aberto sem historico nenhum.
    expect(c.situacao({ negocios: [negocio('aberta')], ganhoEm: null, perdidoEm: null } as never))
      .toBe('aberto');
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
        ganhoEm: '2026-09-17T10:00:00Z', perdidoEm: null, criadoEm: '2026-08-01T10:00:00Z',
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
  /** Abre o modal e responde o que a abertura dispara. `abrirImport` carrega os funis quando a
   *  lista está vazia — o seletor "colocar no funil" precisa deles. */
  function abrirImport(fixture: ComponentFixture<Contatos>) {
    const c = fixture.componentInstance;
    c.abrirImport();
    http.match(r => r.url.endsWith('/pipelines')).forEach(r => r.flush(FUNIS));
    fixture.detectChanges();
    return c;
  }

  /** ⚠️ QUEM VÊ O BOTÃO É QUEM O SERVIDOR DEIXA IMPORTAR, e a primeira versão errou isso: estava
   *  `ehDono`, enquanto o serviço aceita dono OU gestor. O gestor ficava sem o botão de uma
   *  operação permitida a ele.
   *
   *  É o espelho do "botão que sempre erra", e o lado pior: oferecer o que será recusado a pessoa
   *  descobre no clique; esconder o que seria aceito ela nunca descobre — some do produto sem
   *  deixar rastro. */
  it('O BOTÃO DE IMPORTAR SEGUE O MESMO CORTE DO SERVIDOR', () => {
    for (const [papel, esperado] of [
      ['dono', true], ['gestor', true], ['vendedor', false]
    ] as const) {
      const fixture = montar(papel);
      http.match(r => r.url.includes('/contatos')).forEach(r => r.flush(PAGINA_VAZIA));
      fixture.detectChanges();

      const botoes = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.topo button')]
        .map(b => b.textContent!.trim());

      expect(botoes.includes('Importar'))
        .withContext(`${papel} ${esperado ? 'deveria ver' : 'não deveria ver'} "Importar"`)
        .toBe(esperado);

      TestBed.resetTestingModule();
    }
  });

  /** ⚠️ ESCOLHER O ARQUIVO CONFERE, NÃO GRAVA. É o passo que existe porque importar é quase
   *  irreversível: o dono vê "2 novos · 1 já existe" antes de decidir.
   *
   *  Se o `change` do input chamasse a gravação, o "Cancelar" do modal seria uma mentira — e
   *  ninguém descobriria até já ter 800 contatos dentro. */
  it('ESCOLHER O ARQUIVO PEDE A PRÉVIA, E NÃO A GRAVAÇÃO', () => {
    const fixture = montar();
    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush(PAGINA_VAZIA));

    const c = abrirImport(fixture);
    c.escolherArquivo(eventoComArquivo('lista.csv'));

    const pedido = http.expectOne(r => r.url.endsWith('/contatos/importacao/previa'));
    expect(pedido.request.method).toBe('POST');

    // ⚠️ NENHUM pedido para a rota que GRAVA.
    expect(http.match(r => r.url.endsWith('/contatos/importacao')).length).toBe(0);

    pedido.flush(PREVIA);
    fixture.detectChanges();

    expect(c.previa()?.novas).toBe(2);
  });

  /** ⚠️ SEM FUNIL POR PADRÃO — a decisão 1 do bloco. Importar 800 clientes direto para o quadro
   *  seriam 800 cards na primeira etapa, e desfazer é apagar 800 linhas na mão. */
  it('CONFIRMAR NÃO MANDA FUNIL NENHUM POR PADRÃO', () => {
    const fixture = montar();
    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush(PAGINA_VAZIA));

    const c = abrirImport(fixture);
    c.escolherArquivo(eventoComArquivo('lista.csv'));
    http.expectOne(r => r.url.endsWith('/importacao/previa')).flush(PREVIA);
    fixture.detectChanges();

    expect(c.funilDoImport()).withContext('o padrão deixou de ser "sem funil"').toBeNull();

    c.confirmarImport();

    const pedido = http.expectOne(r => r.url.endsWith('/contatos/importacao'));
    expect((pedido.request.body as FormData).get('pipelineId')).toBeNull();
  });

  /** E com funil escolhido ele vai — é a saída de quem QUER os cards, e assume o custo olhando
   *  para o número na tela antes de confirmar. */
  it('COM FUNIL ESCOLHIDO, O ID VAI NO PEDIDO', () => {
    const fixture = montar();
    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush(PAGINA_VAZIA));

    const c = abrirImport(fixture);
    c.escolherArquivo(eventoComArquivo('lista.csv'));
    http.expectOne(r => r.url.endsWith('/importacao/previa')).flush(PREVIA);
    fixture.detectChanges();

    c.funilDoImport.set(9);
    c.confirmarImport();

    const pedido = http.expectOne(r => r.url.endsWith('/contatos/importacao'));
    expect((pedido.request.body as FormData).get('pipelineId')).toBe('9');
  });

  /** ⚠️ A PRÉVIA DESCREVE O ARQUIVO ANTERIOR. Mantê-la na tela ao trocar de arquivo seria oferecer
   *  "Importar 2" sobre números que não são mais daquele arquivo — e o clique gravaria o novo. */
  it('TROCAR DE ARQUIVO JOGA A PRÉVIA FORA', () => {
    const fixture = montar();
    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush(PAGINA_VAZIA));

    const c = abrirImport(fixture);
    c.escolherArquivo(eventoComArquivo('lista.csv'));
    http.expectOne(r => r.url.endsWith('/importacao/previa')).flush(PREVIA);
    fixture.detectChanges();

    expect(c.previa()).not.toBeNull();

    c.escolherArquivo(eventoComArquivo('outra.csv'));
    expect(c.previa()).withContext('a prévia do arquivo antigo ficou na tela').toBeNull();

    http.expectOne(r => r.url.endsWith('/importacao/previa')).flush(PREVIA);
  });

  /** ⚠️ VOLTAR PARA "NÃO COLOCAR" NÃO PODE VIRAR O FUNIL 0 — achado em revisão.
   *
   *  Com `[ngValue]` o evento chega tipado, e `$event === 'null' ? null : +$event` transformava a
   *  opção nula em `+null`, que é 0. Quem escolhia "Vendas" e desistia mandava `pipelineId=0`, e a
   *  importação inteira voltava "Funil não encontrado". */
  it('VOLTAR PARA "NÃO COLOCAR" NÃO VIRA O FUNIL 0', () => {
    const fixture = montar();
    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush(PAGINA_VAZIA));

    const c = abrirImport(fixture);
    c.escolherArquivo(eventoComArquivo('lista.csv'));
    http.expectOne(r => r.url.endsWith('/importacao/previa')).flush(PREVIA);
    fixture.detectChanges();

    const select = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLSelectElement>('#funil-import')!;

    const escolher = (indice: number) => {
      select.value = select.options[indice].value;
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };

    escolher(1);
    expect(c.funilDoImport()).toBe(FUNIS[0].id);

    escolher(0);
    expect(c.funilDoImport()).withContext('"Não colocar" virou um id').toBeNull();

    c.confirmarImport();
    const pedido = http.expectOne(r => r.url.endsWith('/contatos/importacao'));
    expect((pedido.request.body as FormData).get('pipelineId')).toBeNull();
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
