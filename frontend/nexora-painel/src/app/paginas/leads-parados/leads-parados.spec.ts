import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { chaveDia } from '../../nucleo/semaforo';
import { LeadParado, PaginaLeadsParados } from '../../nucleo/servicos/leads-parados.servico';
import { LeadsParados } from './leads-parados';

/** ===================== LEADS PARADOS (LPA-1) =====================
 *
 *  ⚠️ O QUE ESTA SUÍTE GUARDA É A HONESTIDADE DA LEITURA. Três jeitos de a tela mentir sem errar
 *  conta nenhuma:
 *
 *    · lead sem negócio aberto desenhado como se faltasse dado, em vez de travessão;
 *    · um vazio só, que manda esperar quando o problema é o filtro;
 *    · filtro trocado sem voltar à página 1 — a lista some e parece "não há nada".
 *
 *  E, com o lote, duas de a tela mentir sobre o que vai FAZER:
 *
 *    · prometer "em lote" e o operador entender disparo de mensagem;
 *    · seleção que sobrevive à paginação — trinta marcados que ninguém mais vê.
 *  ============================================================== */
describe('leads parados (LPA-1)', () => {
  let fixture: ComponentFixture<LeadsParados>;
  let c: LeadsParados;
  let http: HttpTestingController;

  function lead(over: Partial<LeadParado> = {}): LeadParado {
    return {
      contatoId: 7, nome: 'Joana Prado', telefone: '5584999990000', origem: 'instagram',
      responsavelId: 3, responsavelNome: 'Ana Souza',
      negociacaoId: 41, pipelineNome: 'Vendas', etapaNome: 'Proposta',
      valor: 2500, paradoDesde: '2026-06-01T10:00:00Z', diasParado: 66,
      ...over
    };
  }

  const CHEIA: PaginaLeadsParados = { itens: [lead()], total: 1 };

  const ETIQUETAS = [
    { id: 5, nome: 'reativacao-out', cor: '#2E7A56', contatos: 0 },
    { id: 6, nome: 'cliente vip', cor: '#B4552F', contatos: 0 }
  ];

  const OPCOES = {
    responsaveis: [{ id: 3, nome: 'Ana Souza' }, { id: 4, nome: 'Bruno Lima' }],
    etapas: [], motivosPerda: []
  };

  function montar(papel: 'dono' | 'vendedor' = 'dono', corpo: PaginaLeadsParados = CHEIA) {
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
        id: 1, nome: 'Ana', email: 'a@x.com', papel,
        permissoes: PERMISSOES_DE[papel], empresaNome: 'Padaria'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeadsParados);
    c = fixture.componentInstance;
    fixture.detectChanges();

    // ⚠️ `/etiquetas` RESPONDE ARRAY, nao envelope. Mandar o corpo da lista nele faz o `@for` do
    // seletor estourar com "not iterable" — e o erro seria do teste, nao da tela.
    for (const r of http.match(() => true)) {
      const url = r.request.url;
      if (url.endsWith('/opcoes')) r.flush(OPCOES);
      else if (url.endsWith('/etiquetas')) r.flush(ETIQUETAS);
      else r.flush(corpo);
    }
    fixture.detectChanges();
  }

  function raiz(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function caixas(): HTMLInputElement[] {
    return [...raiz().querySelectorAll<HTMLInputElement>('tbody .sel input')];
  }

  function clicar(seletor: string) {
    raiz().querySelector<HTMLElement>(seletor)!.click();
    fixture.detectChanges();
  }

  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });

  // ==================================================================== a lista

  it('A TABELA MOSTRA O LEAD COM O TEMPO PARADO EM TEXTO', () => {
    montar();

    const linha = raiz().querySelector('.tabela tbody tr')!;

    expect(linha.textContent).toContain('Joana Prado');
    expect(linha.textContent).toContain('Vendas');
    expect(linha.textContent).toContain('Proposta');
    // "há 97 dias" é preciso e não responde "esta pessoa é um caso perdido?".
    expect(linha.querySelector('.parado')!.textContent!.trim()).toBe('há 2 meses');
  });

  it('O TEMPO PARADO VIRA TEXTO QUE SE LÊ, do dia ao mês', () => {
    montar();

    expect(c.tempoParado(0)).toBe('hoje');
    expect(c.tempoParado(1)).toBe('ontem');
    expect(c.tempoParado(12)).toBe('há 12 dias');
    expect(c.tempoParado(30)).toBe('há 1 mês');
    expect(c.tempoParado(95)).toBe('há 3 meses');
  });

  /** ⚠️ SEM NEGÓCIO ABERTO NÃO É DADO FALTANDO. É o lead que entrou por formulário ou importação e
   *  ninguém abriu negócio — o caso mais frio que existe, e o que mais precisa aparecer. Desenhar
   *  célula vazia faria parecer defeito da tela. */
  it('LEAD SEM NEGÓCIO ABERTO DIZ ISSO EM PALAVRAS, e não em célula vazia', () => {
    montar('dono', {
      itens: [lead({ negociacaoId: null, pipelineNome: null, etapaNome: null, valor: null })],
      total: 1
    });

    const linha = raiz().querySelector('.tabela tbody tr')!;

    expect(linha.textContent).toContain('sem negócio aberto');
    // Pela CLASSE, não pelo índice: a coluna de seleção já empurrou as posições uma vez, e um
    // índice fixo quebra de novo na próxima coluna — num teste que não é sobre colunas.
    expect(linha.querySelector('td.num')!.textContent!.trim()).toBe('—');
  });

  // ==================================================================== os filtros

  it('A JANELA TROCA E REFAZ A REQUISIÇÃO, pedindo os dias clicados', () => {
    montar();

    const abas = [...raiz().querySelectorAll('.abas .aba')] as HTMLButtonElement[];
    expect(abas.map(b => b.textContent!.trim())).toEqual(['15 dias', '30 dias', '60 dias', '90 dias']);
    expect(abas[1].getAttribute('aria-pressed')).withContext('30 é o padrão').toBe('true');

    abas[3].click();
    fixture.detectChanges();

    const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.get('dias')).toBe('90');
    req.flush(CHEIA);
  });

  /** ⚠️ TROCAR FILTRO VOLTA PARA A PÁGINA 1. Continuar na página 7 de um recorte que agora tem
   *  duas devolveria uma lista vazia — e o vazio diz "nenhum lead parado", que é mentira. */
  it('TROCAR O FILTRO VOLTA PARA A PRIMEIRA PÁGINA', () => {
    montar('dono', { itens: [lead()], total: 400 });

    c.irPara(5);
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush({ itens: [lead()], total: 400 });
    fixture.detectChanges();
    expect(c.pagina()).toBe(5);

    c.trocarJanela(60);
    const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.get('pagina')).toBe('1');
    req.flush(CHEIA);
    fixture.detectChanges();

    expect(c.pagina()).toBe(1);
  });

  it('O SELETOR DE RESPONSÁVEL MANDA O ID, e "Todos" não manda nada', () => {
    montar();

    c.trocarSeletor('responsavel', '4');
    let req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.get('responsavelId')).toBe('4');
    req.flush(CHEIA);

    c.trocarSeletor('responsavel', '');
    req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.has('responsavelId'))
      .withContext('parâmetro vazio faria o servidor recusar').toBeFalse();
    req.flush(CHEIA);
  });

  /** O seletor vem travado para quem só pode ver a si mesmo — e a trava vem da API, que devolve um
   *  responsável só em `/opcoes`. A tela não decide isso. */
  it('COM UM RESPONSÁVEL SÓ, O SELETOR FICA TRAVADO', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]),
        provideHttpClient(), provideHttpClientTesting()
      ]
    });
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Bia', email: 'b@x.com', papel: 'vendedor',
        permissoes: PERMISSOES_DE.vendedor, empresaNome: 'Padaria'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeadsParados);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/opcoes')) {
        r.flush({ responsaveis: [{ id: 1, nome: 'Bia' }], etapas: [], motivosPerda: [] });
      } else r.flush(CHEIA);
    }
    fixture.detectChanges();

    expect((raiz().querySelector('#f-responsavel') as HTMLSelectElement).disabled).toBeTrue();
  });

  /** ===================== CADA FILTRO NOVO CHEGA NA QUERY STRING =====================
   *  ⚠️ O DEFEITO MAIS SILENCIOSO DE UMA BARRA DE FILTROS e o controle que a tela mostra e nao
   *  aplica: ele parece funcionar, a lista muda por outro motivo, e ninguem desconfia. Um caso
   *  por filtro, afirmando o parametro que saiu.
   *  ============================================================== */
  it('CADA FILTRO SECUNDÁRIO VIRA PARÂMETRO', () => {
    montar();

    const casos: [() => void, string, string][] = [
      [() => c.trocarSeletor('funil', '3'), 'pipelineId', '3'],
      [() => c.trocarSeletor('etapa', '9'), 'etapaId', '9'],
      [() => c.trocarSeletor('origem', 'instagram'), 'origem', 'instagram'],
      [() => c.trocarSeletor('etiqueta', '5'), 'etiquetaId', '5'],
      [() => c.trocarValor('min', '100'), 'valorMin', '100'],
      [() => c.trocarValor('max', '5000'), 'valorMax', '5000']
    ];

    for (const [agir, parametro, esperado] of casos) {
      agir();
      const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
      expect(req.request.params.get(parametro))
        .withContext(`o filtro ${parametro} não chegou na requisição`).toBe(esperado);
      req.flush(CHEIA);
    }
  });

  /** ⚠️ A ETAPA PERTENCE A UM FUNIL. Mantê-la ao trocar de funil daria um recorte que nunca casa
   *  — funil A com etapa do funil B — e a lista viria vazia sem dizer por quê. */
  it('TROCAR O FUNIL LIMPA A ETAPA', () => {
    montar();

    c.trocarSeletor('etapa', '9');
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush(CHEIA);
    expect(c.etapaId()).toBe(9);

    c.trocarSeletor('funil', '3');
    const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.has('etapaId')).withContext('a etapa do outro funil ficou').toBeFalse();
    req.flush(CHEIA);

    expect(c.etapaId()).toBeNull();
  });

  /** ⚠️ A CONSEQUÊNCIA É INVISÍVEL SEM O AVISO: o operador vê a lista encolher e conclui que os
   *  leads sem negócio sumiram do sistema. Filtro de responsável NÃO aciona o aviso — ele é do
   *  contato e do negócio, e não esconde ninguém por ausência de card. */
  it('O AVISO DE RECORTE APARECE SÓ COM FILTRO DE NEGÓCIO', () => {
    montar();

    expect(raiz().querySelector('.aviso-recorte')).toBeNull();

    c.trocarSeletor('responsavel', '4');
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();
    expect(raiz().querySelector('.aviso-recorte'))
      .withContext('responsável não esconde quem não tem negócio').toBeNull();

    c.trocarSeletor('funil', '3');
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();

    expect(raiz().querySelector('.aviso-recorte')!.textContent).toContain('sem negócio aberto');
  });

  it('OS FILTROS SECUNDÁRIOS COMEÇAM FECHADOS', () => {
    montar();

    expect(raiz().querySelector('#f-funil')).toBeNull();

    const botao = raiz().querySelector('.abre-filtros') as HTMLButtonElement;
    expect(botao.textContent!.trim()).toBe('Mais filtros');
    expect(botao.getAttribute('aria-expanded')).toBe('false');

    botao.click();
    fixture.detectChanges();

    expect(raiz().querySelector('#f-funil')).not.toBeNull();
    expect(raiz().querySelector('#f-etiqueta')).not.toBeNull();
  });

  it('LIMPAR DEVOLVE TODOS OS FILTROS AO PADRÃO', () => {
    montar();

    c.trocarSeletor('funil', '3');
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush(CHEIA);
    c.trocarValor('min', '100');
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();

    (raiz().querySelector('.filtros .limpar') as HTMLButtonElement).click();

    const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    for (const p of ['pipelineId', 'etapaId', 'origem', 'etiquetaId', 'valorMin', 'valorMax']) {
      expect(req.request.params.has(p)).withContext(`${p} sobreviveu ao limpar`).toBeFalse();
    }
    req.flush(CHEIA);
  });

  // ==================================================================== o vazio

  /** ⚠️ DOIS VAZIOS, PORQUE MANDAM FAZER COISAS DIFERENTES: um diz que está tudo em dia, o outro
   *  diz que o recorte é apertado. Um texto único faria o dono com um filtro ligado concluir que
   *  não há lead parado nenhum. */
  it('SEM FILTRO, O VAZIO DIZ QUE ESTÁ TUDO EM DIA', () => {
    montar('dono', { itens: [], total: 0 });

    const vazio = raiz().querySelector('.vazio')!.textContent!;
    expect(vazio).toContain('Nenhum lead parado há mais de 30 dias');
    expect(vazio).toContain('movimento recente');
    expect(raiz().querySelector('.tabela')).toBeNull();
  });

  it('COM FILTRO, O VAZIO MANDA AFROUXAR O RECORTE', () => {
    montar('dono', { itens: [], total: 0 });

    c.trocarSeletor('responsavel', '4');
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush({ itens: [], total: 0 });
    fixture.detectChanges();

    const vazio = raiz().querySelector('.vazio')!.textContent!;
    expect(vazio).toContain('com esses filtros');
    expect(vazio).toContain('tire o recorte');
  });

  it('O BOTÃO LIMPAR SÓ APARECE COM FILTRO, e devolve ao padrão', () => {
    montar();

    // ⚠️ `.limpar`, NAO `.btn-neutro`: o botao "Mais filtros" tambem e `.btn-neutro` dentro de
    // `.filtros` desde a entrega 2, e o seletor generico passou a pegá-lo sempre.
    expect(raiz().querySelector('.filtros .limpar')).toBeNull();

    c.trocarJanela(90);
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();

    const limpar = raiz().querySelector('.filtros .limpar') as HTMLButtonElement;
    expect(limpar).not.toBeNull();

    limpar.click();
    const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.get('dias')).toBe('30');
    req.flush(CHEIA);
    fixture.detectChanges();

    expect(c.dias()).toBe(30);
    expect(c.responsavelId()).toBeNull();
  });

  // ==================================================================== a paginação

  it('COM UMA PÁGINA SÓ, A PAGINAÇÃO NÃO APARECE', () => {
    montar();
    expect(raiz().querySelector('app-paginacao button')).toBeNull();
  });

  /** O par. Sem ele, uma versão que escondesse a paginação SEMPRE passaria no teste de cima. */
  it('COM MAIS DE UMA PÁGINA, A PAGINAÇÃO APARECE', () => {
    montar('dono', { itens: [lead()], total: 400 });
    expect(raiz().querySelector('app-paginacao button')).not.toBeNull();
  });

  it('FALHA DE REDE MOSTRA RECADO, e não uma tela em branco', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]),
        provideHttpClient(), provideHttpClientTesting()
      ]
    });
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Padaria'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeadsParados);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) r.error(new ProgressEvent('erro'));
    fixture.detectChanges();

    expect(raiz().querySelector('.erro')!.textContent).toContain('Não foi possível carregar');
  });

  // ==================================================================== a seleção e o lote

  /** ===================== A SELEÇÃO É POR LINHA, E AS DUAS AÇÕES A LEEM DIFERENTE =====================
   *
   *  ⚠️ FOI POR CONTATO E TEVE DE MUDAR. Marcar a pessoa obrigaria a etiqueta a cair nos DOIS
   *  negócios dela, e o negócio que não estava sendo reativado seria creditado à reativação
   *  quando fosse ganho — exatamente o que a etiqueta de negociação existe para evitar.
   *
   *  Então a seleção segue a granularidade da TABELA, e cada ação a reduz: o lembrete deduplica
   *  por contato (uma tarefa por pessoa), a etiqueta usa as negociações marcadas.
   *  ================================================================================================= */
  it('AS DUAS LINHAS DA MESMA PESSOA MARCAM SEPARADO, E O LEMBRETE VIRA UM SÓ', () => {
    montar('dono', {
      itens: [
        lead({ negociacaoId: 41, pipelineNome: 'Vendas' }),
        lead({ negociacaoId: 42, pipelineNome: 'Pós-venda' })
      ],
      total: 2
    });

    expect(caixas().length).withContext('duas linhas, duas caixinhas').toBe(2);

    caixas()[0].click();
    fixture.detectChanges();

    expect(caixas().map(i => i.checked))
      .withContext('a linha de Vendas, não as duas').toEqual([true, false]);

    caixas()[1].click();
    fixture.detectChanges();

    expect(c.quantosMarcados()).withContext('dois negócios parados').toBe(2);
    expect(c.negociacoesMarcadas()).toEqual([41, 42]);

    // ...e o lembrete é UM, porque é a mesma pessoa.
    expect(c.contatosMarcados()).withContext('uma pessoa, uma tarefa').toEqual([7]);

    const barra = raiz().querySelector('.barra-lote')!.textContent!;
    expect(barra).toContain('2 leads selecionados');
    expect(barra).withContext('a diferença aparece, senão o resultado surpreende')
      .toContain('1 contatos');
  });

  it('SEM O GESTO DE AGIR EM LOTE, A COLUNA DE SELEÇÃO NÃO EXISTE', () => {
    // Oferecer a caixinha e recusar no fim é pior que não oferecer: a pessoa marca cinquenta
    // leads para descobrir no botão que não pode.
    montar('vendedor');

    expect(c.podeAgirEmLote()).toBeFalse();
    expect(caixas().length).withContext('nem cabeçalho nem célula').toBe(0);
    expect(raiz().querySelector('thead .sel')).toBeNull();
    expect(raiz().querySelector('.barra-lote')).toBeNull();
  });

  /** ⚠️ MARCAR TODOS É A PÁGINA, NUNCA O RESULTADO TODO: o servidor aceita no máximo uma página
   *  por chamada, e um botão que marcasse 300 prometeria uma ação que volta 400. */
  it('MARCAR TODOS MARCA A PÁGINA, NÃO O RESULTADO INTEIRO', () => {
    // ⚠️ `negociacaoId` DISTINTO EM CADA LINHA. A chave da seleção é a da negociação, e dois
    // leads com o mesmo id de negócio são UMA linha para ela — a primeira versão deste teste
    // tinha a fixture repetindo 41 e media uma seleção de um.
    montar('dono', {
      itens: [
        lead({ contatoId: 7, negociacaoId: 41 }),
        lead({ contatoId: 8, nome: 'Bruno', negociacaoId: 42 })
      ],
      total: 300
    });

    clicar('thead .sel input');

    expect(c.quantosMarcados()).withContext('os dois da página, não os 300').toBe(2);
    expect(c.todosMarcados()).toBeTrue();

    clicar('thead .sel input');
    expect(c.quantosMarcados()).withContext('o mesmo clique desmarca').toBe(0);
  });

  /** ===================== SELEÇÃO INVISÍVEL É O PIOR DEFEITO DAQUI =====================
   *
   *  ⚠️ Trinta marcados na página 1 que o operador não vê mais e não lembra, somados aos da
   *  página 2, passam do teto do servidor — e o 400 chega no fim de um trabalho já feito.
   *  ==================================================================================== */
  it('TROCAR DE PÁGINA APAGA A SELEÇÃO', () => {
    montar('dono', { itens: [lead()], total: 120 });

    clicar('tbody .sel input');
    expect(c.quantosMarcados()).toBe(1);

    c.irPara(2);
    http.expectOne(r => r.url.includes('/leads-parados') && r.params.get('pagina') === '2')
      .flush({ itens: [lead({ contatoId: 9, nome: 'Carla' })], total: 120 });
    fixture.detectChanges();

    expect(c.quantosMarcados()).withContext('a página 1 não vem escondida junto').toBe(0);
  });

  it('TROCAR DE FILTRO APAGA A SELEÇÃO', () => {
    montar('dono', { itens: [lead()], total: 1 });

    clicar('tbody .sel input');
    c.trocarJanela(60);
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();

    expect(c.quantosMarcados()).toBe(0);
  });

  /** ===================== "EM LOTE" NÃO É DISPARO DE MENSAGEM =====================
   *
   *  ⚠️ O WhatsApp roda via Baileys e disparo em massa queima o número do cliente. O operador que
   *  vem de outra ferramenta espera o contrário, e descobrir depois de marcar cinquenta é tarde.
   *  O corpo do POST não tem campo de mensagem — não há como enganar nem por engano.
   *  =============================================================================== */
  it('O MODAL DIZ QUE CRIA TAREFA E NÃO MANDA MENSAGEM, E O CORPO DO POST CONFIRMA', () => {
    montar('dono');

    clicar('tbody .sel input');
    clicar('.criar-lembretes');

    const modal = raiz().querySelector('.overlay .modal')!;
    expect(modal.textContent).toContain('tarefa');
    expect(modal.textContent).toContain('Nenhuma mensagem é enviada');
    expect(modal.querySelector('textarea#lote-obs'))
      .withContext('observação é da tarefa, não texto de envio').not.toBeNull();

    clicar('.confirmar-lote');

    const req = http.expectOne(r => r.url.endsWith('/leads-parados/lembretes'));
    expect(req.request.method).toBe('POST');
    expect(Object.keys(req.request.body as object).sort())
      .withContext('nenhum campo de mensagem, nem vazio')
      .toEqual(['contatoIds', 'dataAlvo', 'observacao', 'titulo']);
    expect((req.request.body as { contatoIds: number[] }).contatoIds).toEqual([7]);

    req.flush({ criados: 1, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados?') || r.url.endsWith('/leads-parados'))
      .flush(CHEIA);
    fixture.detectChanges();
  });

  /** ⚠️ "PULADOS" É O NÚMERO QUE EXPLICA a diferença entre o que foi marcado e o que foi criado, e
   *  ele só aparece DEPOIS de o modal fechar. Um aviso que morre com o modal não é lido. */
  it('O RESULTADO SOBREVIVE AO MODAL E DIZ QUANTOS FORAM PULADOS', () => {
    montar('dono', {
      itens: [
        lead({ contatoId: 7, negociacaoId: 41 }),
        lead({ contatoId: 8, nome: 'Bruno', negociacaoId: 42 })
      ],
      total: 2
    });

    clicar('thead .sel input');
    clicar('.criar-lembretes');
    clicar('.confirmar-lote');

    http.expectOne(r => r.url.endsWith('/leads-parados/lembretes'))
      .flush({ criados: 1, pulados: 1, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();

    expect(raiz().querySelector('.overlay')).withContext('o modal fecha').toBeNull();

    const aviso = raiz().querySelector('.aviso-recorte')!.textContent!;
    expect(aviso).toContain('1');
    expect(aviso).toContain('lembrete criado');
    expect(aviso).toContain('já tinha');
    expect(c.quantosMarcados()).withContext('a seleção sai com a lista nova').toBe(0);
  });

  /** A data vem de `chaveDia`, que é quem carrega a regra do fuso — e `semaforo.spec.ts` a guarda
   *  com um `Date` falso, de modo a morder até num runner em UTC. Aqui só se verifica a ESCOLHA do
   *  padrão: amanhã, e não hoje (que o servidor pode recusar) nem a data vazia. */
  it('A DATA PADRÃO É AMANHÃ', () => {
    montar('dono');

    clicar('tbody .sel input');
    clicar('.criar-lembretes');

    const amanha = new Date();
    amanha.setDate(amanha.getDate() + 1);

    expect(c.loteData()).toBe(chaveDia(amanha));

    clicar('.overlay .btn-neutro');
  });

  it('TÍTULO VAZIO NÃO DEIXA CONFIRMAR', () => {
    montar('dono');

    clicar('tbody .sel input');
    clicar('.criar-lembretes');

    c.loteTitulo.set('   ');
    fixture.detectChanges();

    expect(raiz().querySelector<HTMLButtonElement>('.confirmar-lote')!.disabled)
      .withContext('o servidor recusa, e a tela não deixa chegar lá').toBeTrue();
  });

  // ==================================================================== a etiqueta em lote

  /** ===================== O MODAL DIZ QUE SOMA, PORQUE A OUTRA TELA SUBSTITUI =====================
   *
   *  ⚠️ `PUT /api/etiquetas/negociacoes/{id}/etiquetas` recebe o CONJUNTO FINAL — é assim que o
   *  card do funil aplica etiqueta. Aqui a regra é somar, e supor que o operador vai adivinhar a
   *  diferença é como deixá-lo achar que perdeu "Urgente" de cinquenta cards.
   *  ============================================================================================= */
  it('O MODAL DA ETIQUETA DIZ QUE SOMA, E MANDA AS NEGOCIAÇÕES', () => {
    montar('dono', {
      itens: [
        lead({ contatoId: 7, negociacaoId: 41 }),
        lead({ contatoId: 8, nome: 'Bruno', negociacaoId: 42 })
      ],
      total: 2
    });

    clicar('thead .sel input');
    clicar('.aplicar-etiqueta');

    const modal = raiz().querySelector('.overlay .modal')!;
    expect(modal.textContent).toContain('somada');
    expect(modal.textContent).toContain('Nada é removido');

    c.loteEtiqueta.set(5);
    fixture.detectChanges();
    clicar('.confirmar-etiqueta');

    const req = http.expectOne(r => r.url.endsWith('/leads-parados/etiquetas'));
    expect(req.request.method).toBe('POST');
    expect(req.request.body)
      .withContext('negociacaoIds, não contatoIds — a etiqueta é do negócio')
      .toEqual({ negociacaoIds: [41, 42], etiquetaId: 5 });

    req.flush({ criados: 2, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();
  });

  /** ⚠️ LEAD SEM NEGÓCIO ABERTO NÃO TEM ONDE COLAR, e é o lead frio mais comum. Marcou quinze,
   *  vai marcar doze: sem dizer isso ANTES, o operador vê o resultado menor e procura um defeito. */
  it('A ETIQUETA AVISA QUANTOS FICAM DE FORA POR NÃO TEREM NEGÓCIO', () => {
    montar('dono', {
      itens: [
        lead({ contatoId: 7, negociacaoId: 41 }),
        lead({ contatoId: 8, nome: 'Bruno', negociacaoId: null, pipelineNome: null, etapaNome: null })
      ],
      total: 2
    });

    clicar('thead .sel input');

    expect(c.quantosMarcados()).toBe(2);
    expect(c.alvosDaEtiqueta()).withContext('só o que tem negócio').toBe(1);

    clicar('.aplicar-etiqueta');

    const modal = raiz().querySelector('.overlay .modal')!;
    expect(modal.textContent).toContain('1');
    expect(modal.textContent).toContain('não tem');
    expect(modal.textContent).toContain('negócio aberto');

    clicar('.overlay .btn-neutro');
  });

  /** Nenhum marcado com negócio aberto: o botão não abre nada, e diz por que no `title` em vez de
   *  o clique simplesmente não fazer efeito. */
  it('SEM NENHUM NEGÓCIO ABERTO, O BOTÃO DE ETIQUETA ESTÁ DESABILITADO E EXPLICA', () => {
    montar('dono', {
      itens: [lead({ negociacaoId: null, pipelineNome: null, etapaNome: null })],
      total: 1
    });

    clicar('tbody .sel input');

    const botao = raiz().querySelector<HTMLButtonElement>('.aplicar-etiqueta')!;
    expect(botao.disabled).toBeTrue();
    expect(botao.title).toContain('negócio aberto');
  });

  it('A ETIQUETA COMEÇA VAZIA E SEM ELA NÃO DÁ PARA CONFIRMAR', () => {
    // Pré-escolher a primeira da lista faria aplicar "cliente vip" em cinquenta cards por um
    // clique rápido.
    montar('dono');

    clicar('tbody .sel input');
    clicar('.aplicar-etiqueta');

    expect(c.loteEtiqueta()).toBeNull();
    expect(raiz().querySelector<HTMLButtonElement>('.confirmar-etiqueta')!.disabled).toBeTrue();

    clicar('.overlay .btn-neutro');
  });

  it('UM MODAL POR VEZ: ABRIR O DA ETIQUETA NÃO DEIXA O DO LEMBRETE ABERTO', () => {
    montar('dono');

    clicar('tbody .sel input');
    clicar('.criar-lembretes');
    expect(raiz().querySelectorAll('.overlay').length).toBe(1);
    expect(raiz().querySelector('#lote-titulo')).not.toBeNull();

    clicar('.overlay .btn-neutro');
    clicar('.aplicar-etiqueta');

    expect(raiz().querySelectorAll('.overlay').length).toBe(1);
    expect(raiz().querySelector('#lote-titulo')).withContext('o do lembrete fechou').toBeNull();
    expect(raiz().querySelector('#lote-etiqueta')).not.toBeNull();

    clicar('.overlay .btn-neutro');
  });
});
