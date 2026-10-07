import { registerLocaleData } from '@angular/common';
import ptBr from '@angular/common/locales/pt';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { chaveDia } from '../../nucleo/semaforo';
import { LeadParado, PaginaLeadsParados } from '../../nucleo/servicos/leads-parados.servico';
import { LeadsParados } from './leads-parados';
import { ToastServico } from '../../nucleo/toast/toast.servico';

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
registerLocaleData(ptBr);

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
      motivoPerda: null,
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
        provideHttpClientTesting(),
        // ⚠️ O MESMO LOCALE DE `app.config.ts`. Sem ele o `| number` formata em en-US e o teste
        // mede `12,500.50` — a tela de verdade escreve `12.500,50`, e a asserção estaria
        // guardando o formato errado.
        { provide: LOCALE_ID, useValue: 'pt-BR' }
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

    // ⚠️ ESCOPADO EM `.filtros`: a tela tem DOIS grupos de `.aba` — as de Parados/Perdidos, que
    // trocam a pergunta, e estas, que trocam a janela. `.abas .aba` solto pegava os seis.
    const abas = [...raiz().querySelectorAll('.filtros .abas .aba')] as HTMLButtonElement[];
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

  /** ===================== O ERRO DIZ O MOTIVO =====================
   *  ⚠️ O CASO DA REVISÃO: todo erro do lote virava "Não foi possível… Tente de novo." — data no
   *  passado, mais de 50 leads, sem permissão, alguém inativo. O operador tentava de novo para
   *  sempre. O controller diz que a regra mora no serviço porque "o serviço já devolve a frase
   *  que o operador precisa ler"; a tela jogava a frase fora.
   *
   *  O texto fixo fica só para quando o servidor não manda frase (a rede caiu).
   *  ============================================================== */
  it('O ERRO DO LOTE MOSTRA A FRASE DO SERVIDOR, E O TEXTO FIXO SÓ SEM ELA', () => {
    montar('dono');

    clicar('thead .sel input');
    clicar('.criar-lembretes');
    clicar('.confirmar-lote');

    http.expectOne(r => r.url.endsWith('/leads-parados/lembretes')).flush(
      { erro: 'A data do lembrete não pode ser no passado.' },
      { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(c.erroLote()).toBe('A data do lembrete não pode ser no passado.');

    // Sem frase: a rede caiu.
    clicar('.confirmar-lote');
    http.expectOne(r => r.url.endsWith('/leads-parados/lembretes'))
      .error(new ProgressEvent('erro'), { status: 0, statusText: '' });
    fixture.detectChanges();

    expect(c.erroLote()).toBe('Não foi possível criar os lembretes. Tente de novo.');
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
    expect(c.negociosMarcados()).withContext('só o que tem negócio').toBe(1);

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

  // ==================================================================== a métrica de reativados

  /** Abre o bloco e devolve o pedido, para os testes não repetirem os dois cliques. */
  function abrirMetrica(etiquetaId = 5) {
    clicar('.abre-metrica');

    // Fechado, o bloco não pede nada: sem etiqueta não há pergunta.
    c.metricaEtiqueta.set(etiquetaId);
    c.carregarMetrica();
    fixture.detectChanges();

    return http.expectOne(r => r.url.endsWith('/leads-parados/reativacao'));
  }

  /** ⚠️ O BLOCO COMEÇA FECHADO E NÃO PEDE NADA. A pergunta do dia a dia é "quem parou"; abrir a
   *  tela com um bloco de números empurraria a lista para baixo e gastaria uma requisição que
   *  ninguém pediu. */
  it('A MÉTRICA COMEÇA FECHADA E NÃO FAZ REQUISIÇÃO', () => {
    montar('dono');

    expect(raiz().querySelector('.metrica')).toBeNull();
    expect(c.metrica()).toBeNull();
    // O `http.verify()` do afterEach é quem prova que nada ficou pendurado.
  });

  /** ⚠️ SEM ETIQUETA, O BLOCO PEDE PARA ESCOLHER — não mostra zeros. Quatro zeros na tela parecem
   *  resposta ("a campanha não rendeu nada"), e a pergunta nem tinha sido feita. */
  it('ABERTO SEM ETIQUETA, O BLOCO PEDE PARA ESCOLHER E NÃO PEDE NADA AO SERVIDOR', () => {
    montar('dono');
    clicar('.abre-metrica');

    expect(raiz().querySelector('.metrica')).not.toBeNull();
    expect(raiz().querySelector('.numeros')).withContext('nenhum número ainda').toBeNull();
    expect(raiz().querySelector('.metrica .vazio')!.textContent)
      .toContain('Escolha a etiqueta');
  });

  it('A JANELA PADRÃO É OS ÚLTIMOS 30 DIAS, E O RÓTULO DIZ QUE É DA MARCA', () => {
    montar('dono');
    const req = abrirMetrica();

    const hoje = new Date();
    const trintaAtras = new Date();
    trintaAtras.setDate(trintaAtras.getDate() - 30);

    expect(req.request.params.get('de')).toBe(chaveDia(trintaAtras));
    expect(req.request.params.get('ate')).toBe(chaveDia(hoje));
    expect(req.request.params.get('etiquetaId')).toBe('5');

    // ⚠️ O RÓTULO É "Marcados de", não "De". "De/Até" sozinho seria lido como período da VENDA,
    // que é outra pergunta — e aquela esconderia as reativações ainda em andamento.
    // O rótulo é lido na BARRA inteira: o `<label>` embrulha o input e não tem `for`, e pegar
    // `.campo` solto traz o primeiro da linha, que é o da etiqueta.
    expect(raiz().querySelector('.metrica .linha-filtros')!.textContent)
      .toContain('Marcados de');

    req.flush({ marcados: 0, ganhos: 0, valorGanho: 0, aproveitamentoPercentual: null });
    fixture.detectChanges();
  });

  it('OS QUATRO NÚMEROS APARECEM, E O RÓTULO DIZ "DEPOIS DE MARCADOS"', () => {
    montar('dono');
    const req = abrirMetrica();

    req.flush({ marcados: 40, ganhos: 10, valorGanho: 12500.5, aproveitamentoPercentual: 25 });
    fixture.detectChanges();

    const bloco = raiz().querySelector('.numeros')!.textContent!;

    expect(bloco).toContain('40');
    expect(bloco).toContain('10');
    expect(bloco).withContext('10 de 40').toContain('25%');
    expect(bloco).toContain('12.500,50');

    // ⚠️ "ganhos depois de marcados", não "ganhos": o negócio que já estava ganho quando recebeu
    // a marca não entra na conta do servidor, e o rótulo tem de dizer a mesma coisa que a conta.
    expect(bloco).toContain('depois de marcados');
  });

  /** ⚠️ SEM NADA MARCADO O SERVIDOR MANDA NULL, E A TELA MOSTRA "—". Nem "NaN%", que era o risco
   *  de dividir aqui, nem "0%", que afirmaria que a campanha rodou e não deu nada. */
  it('SEM NADA MARCADO, O APROVEITAMENTO É "—", NUNCA NaN NEM 0%', () => {
    montar('dono');
    const req = abrirMetrica();

    req.flush({ marcados: 0, ganhos: 0, valorGanho: 0, aproveitamentoPercentual: null });
    fixture.detectChanges();

    const bloco = raiz().querySelector('.numeros')!.textContent!;
    expect(bloco).not.toContain('NaN');
    expect(bloco).not.toContain('0%');
    expect(bloco).toContain('—');
  });

  /** ⚠️ A TELA NÃO DIVIDE (AUD-1). O servidor manda 33,33 com 40 marcados e 10 ganhos — números
   *  que dariam 25% se a tela fizesse a conta. É o percentual do servidor que tem de aparecer. */
  it('O APROVEITAMENTO É O DO SERVIDOR, NÃO UMA CONTA DA TELA', () => {
    montar('dono');
    abrirMetrica().flush({ marcados: 40, ganhos: 10, valorGanho: 100, aproveitamentoPercentual: 33.33 });
    fixture.detectChanges();

    const bloco = raiz().querySelector('.numeros')!.textContent!;
    expect(bloco).toContain('33,33%');
    expect(bloco).not.toContain('25%');
  });

  /** ===================== OS DOIS RECORTES TÊM DE SER O MESMO =====================
   *
   *  ⚠️ Com o recorte só na lista, a tela mostraria "os leads parados da Ana" e, logo acima, o
   *  número da equipe inteira — dois recortes diferentes no mesmo olhar, e nada dizendo qual é
   *  qual. O servidor descarta o parâmetro de quem não pode usá-lo; isto é só não divergir.
   *  ============================================================================== */
  it('A MÉTRICA SEGUE O MESMO RESPONSÁVEL DA LISTA', () => {
    montar('dono');
    abrirMetrica().flush({ marcados: 40, ganhos: 10, valorGanho: 100, aproveitamentoPercentual: 25 });
    fixture.detectChanges();

    c.trocarSeletor('responsavel', '4');
    fixture.detectChanges();

    // A lista refaz...
    http.expectOne(r => r.url.includes('/leads-parados') && !r.url.includes('reativacao'))
      .flush(CHEIA);
    fixture.detectChanges();

    // ...e a métrica também, com o mesmo responsável.
    const req = http.expectOne(r => r.url.endsWith('/leads-parados/reativacao'));
    expect(req.request.params.get('responsavelId')).toBe('4');

    req.flush({ marcados: 12, ganhos: 3, valorGanho: 50, aproveitamentoPercentual: 25 });
    fixture.detectChanges();
  });

  /** Aplicar etiqueta MUDA o número de marcados. Deixar o valor velho faria parecer que a ação
   *  não teve efeito — e o operador aplicaria de novo. */
  it('APLICAR ETIQUETA RECALCULA A MÉTRICA', () => {
    montar('dono');
    abrirMetrica().flush({ marcados: 40, ganhos: 10, valorGanho: 100, aproveitamentoPercentual: 25 });
    fixture.detectChanges();

    clicar('tbody .sel input');
    clicar('.aplicar-etiqueta');
    c.loteEtiqueta.set(5);
    fixture.detectChanges();
    clicar('.confirmar-etiqueta');

    http.expectOne(r => r.url.endsWith('/leads-parados/etiquetas'))
      .flush({ criados: 1, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados') && !r.url.includes('reativacao'))
      .flush(CHEIA);
    fixture.detectChanges();

    const req = http.expectOne(r => r.url.endsWith('/leads-parados/reativacao'));
    req.flush({ marcados: 41, ganhos: 10, valorGanho: 100, aproveitamentoPercentual: 24.39 });
    fixture.detectChanges();

    expect(c.metrica()!.marcados).toBe(41);
  });

  it('FALHA NA MÉTRICA NÃO DERRUBA A LISTA', () => {
    // O bloco é um extra: a tela existe para a lista, e um 500 no cálculo não pode esvaziá-la.
    montar('dono');
    abrirMetrica().error(new ProgressEvent('erro'), { status: 500, statusText: 'erro' });
    fixture.detectChanges();

    expect(c.erroMetrica()).not.toBe('');
    expect(c.metrica()).toBeNull();
    expect(raiz().querySelectorAll('.tabela tbody tr').length)
      .withContext('a lista continua de pé').toBe(1);
  });

  // ==================================================================== a aba "Perdidos"

  /** Troca para a aba e devolve a lista pedida, para os testes não repetirem o clique + flush. */
  function irParaPerdidos(corpo: PaginaLeadsParados = {
    itens: [lead({ motivoPerda: 'achou caro' })], total: 1
  }) {
    clicar('.abas-topo .aba:nth-child(2)');

    const req = http.expectOne(
      r => r.url.includes('/leads-parados') && !r.url.includes('reativacao'));

    expect(req.request.params.get('aba')).withContext('a aba vai na query string').toBe('perdidos');

    req.flush(corpo);
    fixture.detectChanges();

    return req;
  }

  it('A ABA PERDIDOS PEDE A OUTRA LISTA E VOLTA PARA A PÁGINA 1', () => {
    montar('dono', { itens: [lead()], total: 400 });

    c.irPara(5);
    http.expectOne(r => r.url.includes('/leads-parados')).flush({ itens: [lead()], total: 400 });
    fixture.detectChanges();

    const req = irParaPerdidos();

    expect(req.request.params.get('pagina'))
      .withContext('a página 5 da outra lista não existe aqui').toBe('1');
    expect(c.perdidos()).toBeTrue();
  });

  /** ===================== A MESMA "30 DIAS" SOBRE DOIS EIXOS =====================
   *
   *  ⚠️ Em "parados" são dias SEM CONVERSA; em "perdidos", dias DESDE A PERDA. Sem dizer qual, o
   *  operador lê o número errado — e a coluna da tabela tem o mesmo problema.
   *  ============================================================================== */
  it('A TELA DIZ O QUE A JANELA CONTA EM CADA ABA', () => {
    montar('dono');

    expect(raiz().querySelector('.janela-diz')!.textContent).toContain('sem conversa');
    expect(raiz().querySelector('thead')!.textContent).toContain('Parado');

    irParaPerdidos();

    expect(raiz().querySelector('.janela-diz')!.textContent).toContain('desde que perdemos');
    expect(raiz().querySelector('thead')!.textContent).toContain('Perdido');
  });

  /** ⚠️ O MOTIVO DA PERDA É A PRIMEIRA INFORMAÇÃO DE QUEM VAI REABRIR: "perdemos por preço" e
   *  "perdemos por prazo" levam a abordagens diferentes, e reabrir sem ler isso é repetir a
   *  conversa que falhou. A coluna só existe nesta aba — em "parados" não houve perda. */
  it('A COLUNA MOTIVO SÓ EXISTE EM PERDIDOS, E O VAZIO VIRA TRAVESSÃO', () => {
    montar('dono');

    expect(raiz().querySelector('td.motivo')).withContext('em parados não há motivo').toBeNull();

    irParaPerdidos({
      itens: [lead({ motivoPerda: 'achou caro' }), lead({ contatoId: 8, negociacaoId: 42, motivoPerda: null })],
      total: 2
    });

    const motivos = [...raiz().querySelectorAll('td.motivo')].map(t => t.textContent!.trim());

    expect(motivos).toEqual(['achou caro', '—']);
  });

  it('O BOTÃO REABRIR SÓ APARECE NA ABA PERDIDOS', () => {
    // Reabrir o que já está aberto não é ação nenhuma.
    montar('dono');

    clicar('tbody .sel input');
    expect(raiz().querySelector('.reabrir')).toBeNull();
    expect(raiz().querySelector('.criar-lembretes')).not.toBeNull();

    irParaPerdidos();
    clicar('tbody .sel input');

    expect(raiz().querySelector('.reabrir')).not.toBeNull();
  });

  /** ⚠️ O CASO DA REVISÃO: na aba Perdidos o negócio marcado é o PERDIDO, e "Mudar responsável"
   *  reescrevia de quem foi a perda nos relatórios. O servidor passou a recusar negócio que não
   *  está aberto; a tela não oferece o caminho — com o negócio perdido marcado, o botão ficava
   *  HABILITADO, porque a linha tem `negociacaoId`. */
  it('MUDAR RESPONSÁVEL NÃO EXISTE NA ABA PERDIDOS', () => {
    montar('dono');

    clicar('tbody .sel input');
    expect(raiz().querySelector('.redistribuir')).withContext('em parados existe').not.toBeNull();

    irParaPerdidos();
    clicar('tbody .sel input');

    expect(raiz().querySelector('.redistribuir')).toBeNull();
  });

  /** O mesmo para a etiqueta: o servidor só etiqueta negócio aberto, e na aba Perdidos o botão
   *  habilitado levava sempre a "20 não foram encontrados". */
  it('APLICAR ETIQUETA NÃO EXISTE NA ABA PERDIDOS', () => {
    montar('dono');

    clicar('tbody .sel input');
    expect(raiz().querySelector('.aplicar-etiqueta')).withContext('em parados existe').not.toBeNull();

    irParaPerdidos();
    clicar('tbody .sel input');

    expect(raiz().querySelector('.aplicar-etiqueta')).toBeNull();
  });

  /** ⚠️ REABRIR MANDA OS CONTATOS, NÃO AS LINHAS. A aba mostra uma linha por PERDA, e quem perdeu
   *  em dois funis aparece duas vezes — mandar o id duas vezes não pode abrir dois negócios. */
  it('REABRIR MANDA OS CONTATOS DISTINTOS E RECARREGA', () => {
    montar('dono');
    irParaPerdidos({
      itens: [
        lead({ negociacaoId: 41, pipelineNome: 'Vendas' }),
        lead({ negociacaoId: 42, pipelineNome: 'Pós-venda' })
      ],
      total: 2
    });

    clicar('thead .sel input');
    expect(c.quantosMarcados()).withContext('duas perdas').toBe(2);

    clicar('.reabrir');

    const req = http.expectOne(r => r.url.endsWith('/leads-parados/reabrir'));
    expect(req.request.method).toBe('POST');
    expect(req.request.body)
      .withContext('uma pessoa, uma reabertura').toEqual({ contatoIds: [7] });

    req.flush({ criados: 1, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush({ itens: [], total: 0 });
    fixture.detectChanges();
  });

  /** ===================== "PULADO" TEM MOTIVO DIFERENTE EM CADA AÇÃO =====================
   *
   *  ⚠️ Lembrete pula quem já tem um pendente; reabrir pula quem não tem funil livre. Dizer só o
   *  número faria o operador procurar a causa errada.
   *  ====================================================================================== */
  it('O RESULTADO DE REABRIR FALA DE FUNIL, NÃO DE LEMBRETE', () => {
    montar('dono');
    irParaPerdidos({
      itens: [lead({ negociacaoId: 41 }), lead({ contatoId: 8, nome: 'Bruno', negociacaoId: 42 })],
      total: 2
    });

    clicar('thead .sel input');
    clicar('.reabrir');

    http.expectOne(r => r.url.endsWith('/leads-parados/reabrir'))
      .flush({ criados: 1, pulados: 1, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush({ itens: [], total: 0 });
    fixture.detectChanges();

    const aviso = raiz().querySelector('.aviso-recorte')!.textContent!;

    expect(aviso).toContain('negócio reaberto');
    expect(aviso).toContain('todos os funis');
    expect(aviso).withContext('nada de lembrete aqui').not.toContain('lembrete');
  });

  /** ⚠️ TROCAR DE ABA APAGA A SELEÇÃO E O RESULTADO ANTERIOR. Os leads marcados em "parados" não
   *  existem na outra lista, e o número do lote anterior fala da lista que saiu da tela. */
  it('TROCAR DE ABA APAGA A SELEÇÃO E O RESULTADO ANTERIOR', () => {
    montar('dono');

    clicar('tbody .sel input');
    clicar('.criar-lembretes');
    clicar('.confirmar-lote');

    http.expectOne(r => r.url.endsWith('/leads-parados/lembretes'))
      .flush({ criados: 1, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();

    expect(raiz().querySelector('.aviso-recorte')).not.toBeNull();

    irParaPerdidos();

    expect(c.quantosMarcados()).toBe(0);
    expect(raiz().querySelector('.aviso-recorte'))
      .withContext('o número da outra lista sai da tela').toBeNull();
  });

  it('O VAZIO DE PERDIDOS MANDA OUTRA COISA QUE O DE PARADOS', () => {
    montar('dono', { itens: [], total: 0 });

    expect(raiz().querySelector('.vazio')!.textContent).toContain('Nenhum lead parado');

    irParaPerdidos({ itens: [], total: 0 });

    const vazio = raiz().querySelector('.vazio')!.textContent!;
    expect(vazio).toContain('Nenhum negócio perdido');
    expect(vazio).toContain('ainda está fresco');
  });

  it('A SUBLINHA DO TÍTULO EXPLICA A ABA, E NÃO REPETE A OUTRA', () => {
    montar('dono');

    expect(raiz().querySelector('.sub')!.textContent).toContain('negócio em aberto');

    irParaPerdidos();

    const sub = raiz().querySelector('.sub')!.textContent!;
    expect(sub).toContain('perdemos');
    expect(sub).withContext('reabrir devolve à etapa onde parou').toContain('etapa');
  });

  /** ===================== CABECALHO E CELULAS TEM DE SER O MESMO NUMERO =====================
   *
   *  ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE NAO DERRUBAVA NADA. Tirei o `@if (perdidos())` do
   *  `<th>Motivo</th>` deixando o `<td>` condicionado, e tudo passou verde — porque os outros
   *  testes olham a CELULA. O defeito real e o desalinhamento: "Motivo" fica em cima da coluna
   *  do Responsavel, e a tabela inteira mente sem errar nenhum dado.
   *
   *  A tabela tem DUAS colunas condicionais — a de selecao (pelo gesto) e a de motivo (pela aba) —,
   *  e as quatro combinacoes passam por aqui.
   *  ====================================================================================== */
  it('AS COLUNAS DO CABEÇALHO E DAS LINHAS SÃO O MESMO NÚMERO, NAS QUATRO COMBINAÇÕES', () => {
    function conferir(onde: string) {
      const ths = raiz().querySelectorAll('.tabela thead th').length;
      const tds = raiz().querySelectorAll('.tabela tbody tr:first-child td').length;

      expect(tds).withContext(`${onde}: ${ths} colunas no cabeçalho e ${tds} na linha`).toBe(ths);
    }

    montar('dono');
    conferir('parados, com o gesto');

    irParaPerdidos();
    conferir('perdidos, com o gesto');

    TestBed.resetTestingModule();

    montar('vendedor');
    conferir('parados, sem o gesto');

    irParaPerdidos();
    conferir('perdidos, sem o gesto');
  });

  // ==================================================================== redistribuir

  /** Monta com duas linhas, marca tudo e abre o modal de responsável. */
  function abrirRedistribuir() {
    montar('dono', {
      itens: [
        lead({ contatoId: 7, negociacaoId: 41 }),
        lead({ contatoId: 8, nome: 'Bruno', negociacaoId: 42 })
      ],
      total: 2
    });

    clicar('thead .sel input');
    clicar('.redistribuir');
  }

  /** ===================== O MODAL DIZ QUE MUDA TODO O PAINEL =====================
   *
   *  ⚠️ O servidor muda TRÊS colunas de dono — a da negociação, a do contato e a da conversa —,
   *  porque `contatos.responsavel_id` alimenta a lista, o card do funil, o filtro e o Meu Dia, e
   *  `conversas.responsavel_id` alimenta a caixa. O operador vê esta lista mudar; ele precisa
   *  saber que o lead trocou de mão em todo lugar, não numa coluna desta tabela.
   *  ============================================================================== */
  it('O MODAL DE RESPONSÁVEL DIZ QUE MUDA TODO O PAINEL, E MANDA AS NEGOCIAÇÕES', () => {
    abrirRedistribuir();

    const modal = raiz().querySelector('.overlay .modal')!;
    expect(modal.textContent).toContain('todo o painel');
    expect(modal.textContent).toContain('caixa de entrada');
    expect(modal.textContent).toContain('Meu Dia');

    c.loteResponsavel.set(4);
    fixture.detectChanges();
    clicar('.confirmar-responsavel');

    const req = http.expectOne(r => r.url.endsWith('/leads-parados/responsavel'));
    expect(req.request.method).toBe('POST');
    expect(req.request.body)
      .withContext('negociacaoIds: a atribuição que os relatórios leem é a da negociação')
      .toEqual({ negociacaoIds: [41, 42], responsavelId: 4 });

    req.flush({ criados: 2, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();
  });

  /** ===================== TRÊS ESTADOS NO SELETOR, NÃO DOIS =====================
   *
   *  ⚠️ Vazio é "ainda não escolhi" e desabilita o botão; "sem-dono" é uma ESCOLHA e habilita.
   *  Com dois estados, devolver o lead ao bolo — metade do uso real, tirar o lead de quem saiu de
   *  férias — seria indistinguível de não ter escolhido nada.
   *  ============================================================================= */
  it('O SELETOR DISTINGUE "NÃO ESCOLHI" DE "SEM RESPONSÁVEL"', () => {
    abrirRedistribuir();

    const botao = () => raiz().querySelector<HTMLButtonElement>('.confirmar-responsavel')!;

    expect(c.loteResponsavel()).toBeUndefined();
    expect(botao().disabled).withContext('nada escolhido ainda').toBeTrue();

    // A opção existe na lista, e é escolha, não ausência.
    const opcoes = [...raiz().querySelectorAll<HTMLOptionElement>('#lote-responsavel option')]
      .map(o => o.value);
    expect(opcoes).toContain('sem-dono');

    c.loteResponsavel.set(null);
    fixture.detectChanges();

    expect(botao().disabled).withContext('"sem responsável" é uma escolha').toBeFalse();

    clicar('.confirmar-responsavel');

    const req = http.expectOne(r => r.url.endsWith('/leads-parados/responsavel'));
    expect((req.request.body as { responsavelId: number | null }).responsavelId).toBeNull();

    req.flush({ criados: 2, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();
  });

  it('SEM NEGÓCIO ABERTO, O BOTÃO DE RESPONSÁVEL ESTÁ DESABILITADO E EXPLICA', () => {
    // Sem negócio não há `negociacoes.responsavel_id` para mover.
    montar('dono', {
      itens: [lead({ negociacaoId: null, pipelineNome: null, etapaNome: null })],
      total: 1
    });

    clicar('tbody .sel input');

    const botao = raiz().querySelector<HTMLButtonElement>('.redistribuir')!;
    expect(botao.disabled).toBeTrue();
    expect(botao.title).toContain('negócio aberto');
  });

  it('SEM O GESTO DE AGIR EM LOTE, NÃO HÁ BOTÃO DE RESPONSÁVEL', () => {
    montar('vendedor');

    expect(raiz().querySelector('.redistribuir')).toBeNull();
    expect(raiz().querySelector('.barra-lote')).toBeNull();
  });

  /** ===================== O AVISO SEGUE A AÇÃO, NÃO A ABA =====================
   *
   *  ⚠️ ESTE TESTE NASCEU DE UM DEFEITO QUE EU MESMO COMITEI. O aviso escolhia a frase pela ABA,
   *  e quatro ações caem nele — então aplicar etiqueta escrevia "2 lembretes criados", que é
   *  falso. O teste anterior não pegou porque conferia só o caminho do lembrete.
   *
   *  E "pulados" tem causa diferente em cada uma: já era dessa pessoa · já tinha a etiqueta · não
   *  tem funil livre · já tinha lembrete pendente. Número sem a causa certa manda o operador
   *  procurar o problema errado.
   *  =========================================================================== */
  it('O AVISO DE CADA AÇÃO FALA DAQUELA AÇÃO, E O "PULADO" DA CAUSA CERTA', () => {
    function avisoDe(acao: 'etiqueta' | 'responsavel'): string {
      montar('dono');
      clicar('tbody .sel input');

      if (acao === 'etiqueta') {
        clicar('.aplicar-etiqueta');
        c.loteEtiqueta.set(5);
      } else {
        clicar('.redistribuir');
        c.loteResponsavel.set(4);
      }

      fixture.detectChanges();
      clicar(acao === 'etiqueta' ? '.confirmar-etiqueta' : '.confirmar-responsavel');

      http.expectOne(r => r.url.includes('/leads-parados/'))
        .flush({ criados: 1, pulados: 1, falhou: 0 });
      http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);
      fixture.detectChanges();

      const texto = raiz().querySelector('.aviso-recorte')!.textContent!;

      expect(texto).withContext(`${acao}: a palavra "lembrete" não cabe aqui`)
        .not.toContain('lembrete');

      TestBed.resetTestingModule();

      return texto;
    }

    const etiqueta = avisoDe('etiqueta');
    expect(etiqueta).toContain('etiquetado');
    expect(etiqueta).withContext('a causa do pulado').toContain('já tinha essa etiqueta');

    const responsavel = avisoDe('responsavel');
    expect(responsavel).toContain('mudou de responsável');
    expect(responsavel).withContext('a causa do pulado').toContain('já era dessa pessoa');
  });

  /** ===================== A GUARDA DO METODO, NAO DO BOTAO =====================
   *
   *  ⚠️ ESTE TESTE NASCEU DE UMA SABOTAGEM QUE NAO DERRUBAVA NADA. Tirei o
   *  `if (escolhido === undefined) return;` do `confirmarResponsavel` e tudo passou verde — porque
   *  os outros testes clicam no BOTAO, e ele continua `[disabled]`.
   *
   *  O botao e UM caminho; o metodo e a porta. Teclado, um `[disabled]` que alguem simplifica, um
   *  `@if` que muda de lugar — qualquer um deles faria a chamada sair sem escolha, e o servidor
   *  receberia um pedido que a tela nunca quis mandar.
   *
   *  `http.verify()` do `afterEach` e quem prova que nada saiu.
   *  =========================================================================== */
  it('CHAMAR AS CONFIRMAÇÕES SEM ESCOLHA NÃO MANDA NADA AO SERVIDOR', () => {
    montar('dono');

    clicar('tbody .sel input');

    // Responsável: nada escolhido.
    c.loteResponsavel.set(undefined);
    c.confirmarResponsavel();

    // Etiqueta: nada escolhido.
    c.loteEtiqueta.set(null);
    c.confirmarEtiqueta();

    // Lembrete: título em branco.
    c.loteTitulo.set('   ');
    c.loteData.set('2030-01-01');
    c.confirmarLote();

    // E sem seleção nenhuma, nenhuma das três sai.
    c.desmarcarTudo();
    c.loteResponsavel.set(4);
    c.loteEtiqueta.set(5);
    c.loteTitulo.set('Retomar');
    c.confirmarResponsavel();
    c.confirmarEtiqueta();
    c.confirmarLote();
    c.reabrirSelecionados();

    fixture.detectChanges();

    expect(c.salvandoLote()).withContext('nenhuma chamada começou').toBeFalse();
  });

  // ==================================================================== o erro diz o motivo (revisão LPA-1)
  /** As outras três ações em lote, pela mesma razão do teste do lembrete: a frase do servidor é a
   *  que diz ao operador o que fazer. Uma por ação, para nenhuma voltar ao texto fixo calada. */
  const erroDoServidor = (frase: string) =>
    [{ erro: frase }, { status: 400, statusText: 'Bad Request' }] as const;

  it('O ERRO DE MUDAR RESPONSÁVEL MOSTRA A FRASE DO SERVIDOR', () => {
    abrirRedistribuir();
    c.loteResponsavel.set(4);
    fixture.detectChanges();
    clicar('.confirmar-responsavel');

    http.expectOne(r => r.url.endsWith('/leads-parados/responsavel'))
      .flush(...erroDoServidor('Escolha alguém da equipe que esteja ativo.'));
    fixture.detectChanges();

    expect(c.erroLote()).toBe('Escolha alguém da equipe que esteja ativo.');
  });

  it('O ERRO DA ETIQUETA EM LOTE MOSTRA A FRASE DO SERVIDOR', () => {
    montar('dono', { itens: [lead({ contatoId: 7, negociacaoId: 41 })], total: 1 });
    clicar('thead .sel input');
    clicar('.aplicar-etiqueta');
    c.loteEtiqueta.set(5);
    fixture.detectChanges();
    clicar('.confirmar-etiqueta');

    http.expectOne(r => r.url.endsWith('/leads-parados/etiquetas'))
      .flush(...erroDoServidor('Essa etiqueta não existe mais.'));
    fixture.detectChanges();

    expect(c.erroLote()).toBe('Essa etiqueta não existe mais.');
  });

  it('O ERRO DE REABRIR MOSTRA A FRASE DO SERVIDOR', () => {
    montar('dono');
    irParaPerdidos();
    clicar('thead .sel input');
    clicar('.reabrir');

    http.expectOne(r => r.url.endsWith('/leads-parados/reabrir'))
      .flush(...erroDoServidor('Você não pode agir sobre vários leads de uma vez. Peça ao dono.'));
    fixture.detectChanges();

    expect(c.erro()).toBe('Você não pode agir sobre vários leads de uma vez. Peça ao dono.');
  });

  /** A lista e a métrica também: um 400 de filtro inválido diz qual filtro, e a métrica diz por
   *  que não calculou. Mudar de página dispara a carga de novo — é por ali que o 400 chega. */
  it('O ERRO DA LISTA MOSTRA A FRASE DO SERVIDOR', () => {
    montar('dono', { itens: [lead()], total: 400 });

    c.irPara(2);
    http.expectOne(r => r.url.includes('/leads-parados'))
      .flush(...erroDoServidor('Janela inválida. Use 7, 15, 30, 60 ou 90 dias.'));
    fixture.detectChanges();

    expect(c.erro()).toBe('Janela inválida. Use 7, 15, 30, 60 ou 90 dias.');
  });

  it('O ERRO DA MÉTRICA MOSTRA A FRASE DO SERVIDOR', () => {
    montar('dono');

    abrirMetrica().flush(...erroDoServidor('Essa etiqueta não existe mais.'));
    fixture.detectChanges();

    expect(c.erroMetrica()).toBe('Essa etiqueta não existe mais.');
  });

  /** ===================== A ÚLTIMA PÁGINA QUE ESVAZIOU =====================
   *  ⚠️ O CASO DA REVISÃO: o total vem da própria página, e página vazia chega com `total = 0`.
   *  Reabrir tudo na última página e recarregar fazia a tela dizer que não havia nada, com as
   *  páginas anteriores cheias. Agora ela volta uma página e pede de novo.
   *  ====================================================================== */
  it('PÁGINA QUE ESVAZIOU VOLTA PARA A ANTERIOR, E NÃO DIZ QUE NÃO HÁ NADA', () => {
    montar('dono', { itens: [lead()], total: 400 });

    c.irPara(3);
    http.expectOne(r => r.url.includes('/leads-parados')).flush({ itens: [lead()], total: 400 });
    fixture.detectChanges();

    // Algo tirou as linhas da página 3: ela volta vazia, e com total zero.
    c.carregar();
    const vazia = http.expectOne(r => r.url.includes('/leads-parados'));
    expect(vazia.request.params.get('pagina')).toBe('3');
    vazia.flush({ itens: [], total: 0 });

    const anterior = http.expectOne(r => r.url.includes('/leads-parados'));
    expect(anterior.request.params.get('pagina')).withContext('volta uma página').toBe('2');
    anterior.flush({ itens: [lead()], total: 399 });
    fixture.detectChanges();

    expect(c.pagina()).toBe(2);
    expect(c.total()).toBe(399);
    expect(c.itens().length).toBe(1);
  });

  /** Na página 1, vazio é vazio: não há para onde voltar, e o laço tem de parar. */
  it('NA PRIMEIRA PÁGINA, VAZIO É VAZIO', () => {
    montar('dono', { itens: [], total: 0 });

    expect(c.pagina()).toBe(1);
    expect(c.itens().length).toBe(0);
    http.expectNone(r => r.url.includes('/leads-parados') && !r.url.includes('reativacao'));
  });

  // ==================================================================== o retorno das ações (relato de uso)
  /** ===================== A ETIQUETA APARECE NA LINHA =====================
   *  ⚠️ O RELATO DE USO: "adicionei etiqueta e não mostrou nada". Ela tinha sido gravada; a tabela
   *  é que não tinha a coluna. Os chips são os da ficha do contato; sem etiqueta, travessão.
   *  ===================================================================== */
  it('A COLUNA DE ETIQUETAS MOSTRA OS CHIPS DO NEGÓCIO, E TRAVESSÃO SEM ELES', () => {
    montar('dono', {
      itens: [
        lead({ contatoId: 7, negociacaoId: 41, etiquetas: [{ id: 4, nome: 'Retenção', cor: '#2E7A56' }] }),
        lead({ contatoId: 8, nome: 'Bruno', negociacaoId: 42 })
      ],
      total: 2
    });

    expect([...raiz().querySelectorAll('thead th')].map(t => t.textContent!.trim())).toContain('Etiquetas');

    const linhas = [...raiz().querySelectorAll('tbody tr')];
    const chip = linhas[0].querySelector('.etiquetas-linha .chip') as HTMLElement;
    expect(chip.textContent!.trim()).toBe('Retenção');
    expect(chip.style.background).withContext('a cor da etiqueta').toContain('46, 122, 86');

    expect(linhas[1].querySelector('.etiquetas-linha')!.textContent!.trim()).toBe('—');
  });

  /** ⚠️ O OUTRO RELATO: "criei o lembrete e não fez nada". Os lembretes estavam no banco — para
   *  AMANHÃ, no Meu Dia de quem cuida do lead. O aviso diz as duas coisas. */
  it('O LEMBRETE EM LOTE AVISA A DATA E ONDE ELE VAI APARECER', () => {
    montar('dono', {
      itens: [lead({ contatoId: 7, negociacaoId: 41 }), lead({ contatoId: 8, nome: 'Bruno', negociacaoId: 42 })],
      total: 2
    });
    const toast = TestBed.inject(ToastServico);
    spyOn(toast, 'sucesso');

    clicar('thead .sel input');
    clicar('.criar-lembretes');
    c.loteData.set('2026-10-08');
    fixture.detectChanges();
    clicar('.confirmar-lote');

    http.expectOne(r => r.url.endsWith('/leads-parados/lembretes')).flush({ criados: 2, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);

    expect(toast.sucesso).toHaveBeenCalledOnceWith(
      '2 lembretes criados para 08/10. Aparecem no Meu Dia de quem cuida de cada lead, nesse dia.');
  });

  it('A ETIQUETA EM LOTE AVISA O NOME DELA', () => {
    montar('dono', { itens: [lead({ contatoId: 7, negociacaoId: 41 })], total: 1 });
    const toast = TestBed.inject(ToastServico);
    spyOn(toast, 'sucesso');
    c.etiquetas.set([{ id: 5, nome: 'Retenção', cor: '#2E7A56' } as never]);

    clicar('thead .sel input');
    clicar('.aplicar-etiqueta');
    c.loteEtiqueta.set(5);
    fixture.detectChanges();
    clicar('.confirmar-etiqueta');

    http.expectOne(r => r.url.endsWith('/leads-parados/etiquetas')).flush({ criados: 1, pulados: 0, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);

    expect(toast.sucesso).toHaveBeenCalledOnceWith('Etiqueta “Retenção” aplicada em 1 negócio.');
  });

  /** Nada criado não é sucesso: todos já tinham lembrete pendente, por exemplo. O aviso vira
   *  informação e manda ler o detalhe, que está acima da lista. */
  it('NADA CRIADO VIRA INFORMAÇÃO, E NÃO SUCESSO', () => {
    montar('dono', { itens: [lead({ contatoId: 7, negociacaoId: 41 })], total: 1 });
    const toast = TestBed.inject(ToastServico);
    spyOn(toast, 'sucesso');
    spyOn(toast, 'info');

    clicar('thead .sel input');
    clicar('.criar-lembretes');
    clicar('.confirmar-lote');

    http.expectOne(r => r.url.endsWith('/leads-parados/lembretes')).flush({ criados: 0, pulados: 1, falhou: 0 });
    http.expectOne(r => r.url.includes('/leads-parados')).flush(CHEIA);

    expect(toast.sucesso).not.toHaveBeenCalled();
    expect(toast.info).toHaveBeenCalledOnceWith('Nada mudou: veja o motivo no aviso acima da lista.');
  });
});
