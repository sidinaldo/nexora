import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { FatiaOrigemDto, FunilNoPainelDto, StatusPainel } from '../../nucleo/modelos';
import { Dashboard } from './dashboard';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';

/** O DASHBOARD: funil e rosca.
 *
 *  As três coisas que este arquivo protege são as que passam por qualquer revisão sem serem
 *  notadas: o número de etapas vindo da API (e não cinco escritas no código), a paleta restrita
 *  ao verde, e os percentuais somando exatamente 100%. */
describe('Dashboard — funil e rosca', () => {
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

  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso }
      ]
    });
    httpMock = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel: 'dono', permissoes: PERMISSOES_DE.dono, empresaNome: 'X' }
    } as never);
  });

  afterEach(() => localStorage.clear());

  function funilDe(
    pipelineId: number, nome: string, emNegociacao: number,
    valorEmAberto = 0, ganhasNoMes = 0, conversaoPercentual: number | null = null
  ): FunilNoPainelDto {
    return {
      pipelineId, nome, cor: '#7FA88B', emNegociacao, valorEmAberto, ganhasNoMes, conversaoPercentual
    };
  }

  /** Uma fatia como o SERVIDOR manda (AUD-XX): já somada por origem, com o percentual pronto. */
  function fatiaDe(
    origem: FatiaOrigemDto['origem'], leads: number, percentual: number,
    extra: Partial<FatiaOrigemDto> = {}
  ): FatiaOrigemDto {
    return { origem, agrupada: false, leads, percentual, campanhas: [], ...extra };
  }

  /** ===================== A CAMPANHA NÃO É UMA ORIGEM =====================
   *
   *  "Promoção de Julho" é um link de WhatsApp distribuído NO Instagram. A origem é Instagram; a
   *  campanha é a peça dentro dela. `canais_captacao.origem` é escolhida ao criar o canal e o
   *  contato herda dela — o modelo sempre disse isso.
   *
   *  ⚠️ A PRIMEIRA VERSÃO TROCOU O RÓTULO DA FATIA pelo nome da campanha, e aquilo achatava a
   *  hierarquia: com duas campanhas no Instagram apareceriam duas fatias, e o "quanto o Instagram
   *  me traz" — a pergunta que esta rosca existe para responder — sumia da tela.
   *
   *  Estes testes travam as duas metades: a fatia soma por ORIGEM, e a campanha aparece embaixo.
   *  ======================================================================== */
  describe('a campanha é sub-linha da origem, não fatia', () => {
    // ⚠️ A SOMA POR ORIGEM SAIU DESTA TELA (AUD-XX). O servidor manda UMA fatia por origem, com as
    // campanhas dentro (`ServicoDashboard.Rosca`, coberto por `RoscaDoPainelTests`). O que fica
    // aqui é a tela não achatar a hierarquia de volta: a campanha não vira fatia.
    const INSTAGRAM_COM_CAMPANHAS: FatiaOrigemDto[] = [
      fatiaDe('instagram', 12, 60, {
        campanhas: [{ nome: 'Promoção de Julho', leads: 6 }, { nome: 'Sorteio de Agosto', leads: 4 }]
      }),
      fatiaDe('whatsapp', 8, 40)
    ];

    it('UMA fatia por origem, como o servidor manda — a campanha não vira fatia', () => {
      const fixture = montar([], INSTAGRAM_COM_CAMPANHAS);
      const c = fixture.componentInstance;

      const rotulos = c.fatias().map(f => f.rotulo);
      expect(rotulos).withContext('campanha virou fatia').not.toContain('Promoção de Julho');
      expect(rotulos.filter(r => r === 'Instagram').length)
        .withContext('o Instagram tem que ser UMA fatia').toBe(1);

      const instagram = c.fatias().find(f => f.rotulo === 'Instagram')!;
      expect(instagram.origem.leads).toBe(12);
      expect(instagram.origem.campanhas.map(k => k.nome))
        .toEqual(['Promoção de Julho', 'Sorteio de Agosto']);
    });

    /** ⚠️ O TOTAL É O DO SERVIDOR. Aqui as fatias somam 20 e o servidor diz 37 — números que não
     *  aconteceriam juntos de verdade, e é por isso que provam de onde a tela tira o número. */
    it('O TOTAL DA ROSCA É O `leadsTotal` DO SERVIDOR, e não a soma das fatias', () => {
      const fixture = montar([funilDe(1, 'A', 10)], INSTAGRAM_COM_CAMPANHAS, undefined, { leadsTotal: 37 });

      const topo = [...fixture.nativeElement.querySelectorAll('.cartao-topo')]
        .find(e => (e as Element).textContent!.includes('De onde vêm seus leads')) as HTMLElement;

      expect(topo.querySelector('.mono')!.textContent!.trim()).toBe('37');
    });
  });

  /** Monta a tela e responde as três chamadas do `ngOnInit`. */
  /** Os sinais de estreia (POS-1) nascem LIGADOS aqui, e isso é a escolha certa para um helper
   *  de teste: o caso comum é uma empresa que já opera, e é o payload que todo teste deste arquivo
   *  quer. Com eles desligados por omissão, cada teste de número cairia no aviso de boas-vindas e
   *  falharia por um motivo que não tem nada a ver com o que ele mede. */
  type Sinais = { recebeuMensagem: boolean; temContato: boolean };

  /** Os totais que o SERVIDOR manda. Por padrão, coerentes com as linhas e as fatias — o teste
   *  que quer provar de onde a tela tira o número passa um valor que não bate. */
  type Totais = {
    totalEmNegociacao?: number; totalValorEmAberto?: number; leadsTotal?: number;
    taxaConversaoPercentual?: number | null;
  };

  function montar(
    funil: FunilNoPainelDto[], origens: FatiaOrigemDto[],
    sinais: Sinais = { recebeuMensagem: true, temContato: true },
    totais: Totais = {}
  ): ComponentFixture<Dashboard> {
    const fixture = TestBed.createComponent(Dashboard);
    fixture.detectChanges();

    for (const r of httpMock.match(() => true)) {
      const url = r.request.url;
      if (url.includes('/dashboard/serie')) {
        r.flush({ de: '', ate: '', agrupamento: 'dia', pontos: [] });
      } else if (url.includes('/dashboard/atividades')) {
        r.flush({ itens: [], temMais: false });
      } else if (url.includes('/meu-dia')) {
        r.flush({ acoes: [], respondendo: 0, lembretes: 0, total: 0 });
      } else {
        r.flush({
          leadsHoje: 3, aguardandoResposta: 2, followUpsPendentes: 1,
          vendasDoMes: 4, faturamentoDoMes: 1000, taxaConversaoPercentual: 50,
          funil, origens,
          totalEmNegociacao: funil.reduce((s, f) => s + f.emNegociacao, 0),
          totalValorEmAberto: funil.reduce((s, f) => s + f.valorEmAberto, 0),
          leadsTotal: origens.reduce((s, o) => s + o.leads, 0),
          ...totais,
          ...sinais
        });
      }
    }
    fixture.detectChanges();
    return fixture;
  }

  describe('os funis vêm da API', () => {
    /** ===================== POR QUE ISTO É TESTE =====================
     *  A empresa pode ter um funil ou cinco (`ServicoPipelines.MaximoPipelines`). Um `@for` sobre a
     *  resposta parece obviamente certo e continua certo por acidente enquanto todo mundo tiver um
     *  — e foi exatamente assim que o defeito do FUN-1 atravessou a suíte inteira: com UMA pipeline
     *  no cenário, nada distingue "uma linha por funil" de "as etapas de todos os funis juntas".
     *  =============================================================== */
    it('UMA LINHA POR FUNIL que a API devolver, nem uma a mais', () => {
      for (const quantos of [1, 2, 5]) {
        TestBed.resetTestingModule();
        TestBed.configureTestingModule({
          providers: [
            provideZonelessChangeDetection(), provideRouter([]),
            provideHttpClient(), provideHttpClientTesting(),
            { provide: RealtimeServico, useClass: RealtimeFalso }
          ]
        });
        httpMock = TestBed.inject(HttpTestingController);
        TestBed.inject(AuthServico).aplicarLogin({
          token: 't',
          usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel: 'dono', permissoes: PERMISSOES_DE.dono, empresaNome: 'X' }
        } as never);

        const funis = Array.from({ length: quantos },
          (_, i) => funilDe(i + 1, `Funil ${i + 1}`, 20 - i * 2));

        const fixture = montar(funis, [fatiaDe('site', 5, 100)]);
        const linhas = fixture.nativeElement
          .querySelectorAll('.tabela-funis tbody tr:not(.linha-todos)');

        expect(linhas.length).withContext(`${quantos} funis na API`).toBe(quantos);
      }
    });

    /** ===================== O LINK QUE IA PARA O QUADRO ERRADO (FUN-1) =====================
     *  A faixa apontava para `/funil`, que redireciona para `/crm` SEM id — e `/crm` abre sempre a
     *  pipeline PADRÃO. Clicar numa etapa do Atacado abria o quadro de Vendas, e o `?etapa=` não
     *  achava nada para destacar. Nenhum erro, nenhum aviso: o dono concluía que o card tinha
     *  sumido.
     *
     *  ⚠️ O SEGUNDO `expect` É O QUE PEGA A REGRESSÃO. Só checar o `/crm/9` passaria com um href
     *  montado a partir do índice da lista em vez do `pipelineId` — aqui os dois diferem de
     *  propósito (ids 7 e 9 em posições 0 e 1).
     *  ===================================================================================== */
    it('cada linha leva ao quadro DAQUELE funil, e não ao padrão', () => {
      const fixture = montar(
        [funilDe(7, 'Vendas', 10), funilDe(9, 'Atacado', 4)],
        [fatiaDe('site', 5, 100)]);

      const links = fixture.nativeElement.querySelectorAll('.tabela-funis tbody a');

      expect((links[0] as HTMLAnchorElement).getAttribute('href')).toContain('/crm/7');
      expect((links[1] as HTMLAnchorElement).getAttribute('href')).toContain('/crm/9');
    });

    /** ===================== DOIS RECORTES NA MESMA TABELA =====================
     *  Duas colunas são de AGORA e duas são DO MÊS. Sem o rótulo em cada uma, o dono soma a coluna
     *  "Em negociação" e compara com "Vendas do mês" lá em cima — e conclui que os números do
     *  sistema não batem, que num produto de controle de dados é o pior desfecho possível.
     *
     *  O cartão antigo já declarava o recorte ("situação agora · igual ao quadro"); ele não podia
     *  se perder na troca de forma. Só mudou de lugar, porque agora são dois e não um.
     *  ========================================================================= */
    it('CADA COLUNA DIZ SE É AGORA OU NO MÊS', () => {
      const fixture = montar(
        [funilDe(7, 'Vendas', 10)], [fatiaDe('site', 5, 100)]);

      // O recorte é afirmado no PRÓPRIO elemento, não no texto corrido do cabeçalho: o que faz a
      // coluna ser lida é ele sair numa linha de baixo, em tom fraco — colado no rótulo viraria
      // "Em negociaçãoagora" e ninguém leria nada.
      const colunas = [...fixture.nativeElement.querySelectorAll('.tabela-funis thead th')]
        .map(th => {
          const recorte = (th as HTMLElement).querySelector('.recorte');
          const rotulo = (th as HTMLElement).textContent!
            .replace(recorte?.textContent ?? '', '').trim();
          return [rotulo, recorte?.textContent?.trim() ?? null];
        });

      expect(colunas).toEqual([
        ['Funil', null],
        ['Em negociação', 'agora'],
        ['Valor em aberto', 'agora'],
        ['Ganhas', 'no mês'],
        ['Conversão', 'no mês']
      ]);
    });

    /** ===================== A LINHA "TODOS" NÃO PODE DISCORDAR DO TOPO =====================
     *  Conversão não é somável: é ganhas ÷ encerradas DO CONJUNTO, não a média das linhas. Com dois
     *  funis a 100% e a 0%, a média daria 50% e o valor verdadeiro pode ser qualquer coisa.
     *
     *  ⚠️ POR ISSO AS DUAS COLUNAS DO MÊS SAEM DO KPI, e este teste é o que trava: o payload traz
     *  `vendasDoMes: 4` e conversão de 50% enquanto as linhas somam 3 ganhas e teriam média 75%.
     *  Qualquer versão que recalcule a partir das linhas falha aqui.
     *
     *  ⚠️ E A SOMA DE "EM NEGOCIAÇÃO" TAMBÉM VEM PRONTA (AUD-XX). As linhas somam 15 e o servidor diz
     *  16 — valores que não aconteceriam juntos, e é o que prova que a tela não soma mais nada.
     *  ===================================================================================== */
    it('a linha "Todos" é a do servidor, e não uma conta da tela', () => {
      const fixture = montar(
        [funilDe(7, 'Vendas', 10, 0, 2, 100), funilDe(9, 'Atacado', 5, 0, 1, 50)],
        [fatiaDe('site', 5, 100)], undefined, { totalEmNegociacao: 16 });

      const celulas = [...fixture.nativeElement.querySelectorAll('.linha-todos th, .linha-todos td')]
        .map(c => (c as HTMLElement).textContent!.trim());

      expect(celulas[1]).toBe('16');     // o total do servidor, NÃO os 15 das linhas
      expect(celulas[3]).toBe('4');      // o KPI, NÃO os 3 das linhas
      expect(celulas[4]).toBe('50%');    // o KPI, NÃO a média 75% das linhas
    });

    /** Nada decidido no mês: o servidor manda null, e a tela mostra "—" — nunca "0%" (AUD-XX). */
    it('CONVERSÃO SEM NADA DECIDIDO É "—", e não 0%', () => {
      const fixture = montar(
        [funilDe(7, 'Vendas', 10)], [fatiaDe('site', 5, 100)], undefined,
        { taxaConversaoPercentual: null });

      const celulas = [...fixture.nativeElement.querySelectorAll('.linha-todos th, .linha-todos td')]
        .map(c => (c as HTMLElement).textContent!.trim());

      expect(celulas[4]).toBe('—');
    });
  });

  describe('a rosca de origens', () => {
    /** Nove origens, como o SERVIDOR as manda: as cinco maiores e uma fatia agrupada com o resto
     *  (4 + 3 + 2 + 1), percentuais já somando 100 (AUD-XX). */
    const seisFatias: FatiaOrigemDto[] = [
      fatiaDe('instagram', 15, 25), fatiaDe('whatsapp', 13, 21.67), fatiaDe('indicacao', 10, 16.67),
      fatiaDe('google', 7, 11.67), fatiaDe('site', 5, 8.33),
      fatiaDe('outros', 10, 16.66, { agrupada: true })
    ];

    it('SÓ TONS DE VERDE (mais o creme do "Outros") — nada de azul, vermelho ou laranja', () => {
      // ===================== A RESTRIÇÃO DE PALETA =====================
      // Verde, creme e UM tom de alerta. A exceção acordada são os três estados do semáforo,
      // onde a cor É a informação. Numa rosca a cor é só rótulo — sair da paleta por comodidade
      // é como se perde a identidade de um produto, um gráfico de cada vez.
      // ================================================================
      const fixture = montar([funilDe(1, 'A', 10)], seisFatias);
      const fills = [...fixture.nativeElement.querySelectorAll('.rosca path')]
        .map(p => (p as Element).getAttribute('fill')!);

      expect(fills.length).toBeGreaterThan(0);

      for (const hex of fills) {
        const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
        // Verde: o canal G domina. Creme (o "Outros"): os três canais próximos e claros.
        const ehVerde = g > r && g > b;
        const ehCreme = Math.max(r, g, b) - Math.min(r, g, b) < 30 && r > 150;
        expect(ehVerde || ehCreme).withContext(`${hex} está fora da paleta`).toBeTrue();
      }
    });

    it('A FATIA AGRUPADA DO SERVIDOR SAI COMO "Outros", NO TOM CREME', () => {
      // Quem agrupa é o servidor; a tela rotula pela marca `agrupada` — e não pelo nome, que é
      // `outros` para não colidir com a origem `outro` de verdade.
      const fixture = montar([funilDe(1, 'A', 10)], seisFatias);
      const nomes = [...fixture.nativeElement.querySelectorAll('.legenda .legenda-nome')]
        .map(e => (e as Element).textContent!.trim());

      expect(nomes.length).toBe(6);
      expect(nomes[nomes.length - 1]).toBe('Outros');

      const fills = [...fixture.nativeElement.querySelectorAll('.rosca path')]
        .map(p => (p as Element).getAttribute('fill'));
      expect(fills[fills.length - 1]).toBe('#CFC9B8');
    });

    /** ⚠️ O PERCENTUAL DA LEGENDA É O DO SERVIDOR (AUD-XX). Antes a tela arredondava cada fatia e
     *  empurrava a diferença para a última, para fechar 100. O servidor já manda fechado — três
     *  terços como 33,34 + 33,33 + 33,33 — e a tela só escreve. */
    it('O PERCENTUAL DA LEGENDA É O DO SERVIDOR, e soma 100%', () => {
      const fixture = montar([funilDe(1, 'A', 10)],
        [fatiaDe('site', 1, 33.34), fatiaDe('google', 1, 33.33), fatiaDe('manual', 1, 33.33)]);

      const valores = [...fixture.nativeElement.querySelectorAll('.legenda .legenda-valor')]
        .map(e => (e as Element).textContent!.trim());

      expect(valores).toEqual(['1 · 33,34%', '1 · 33,33%', '1 · 33,33%']);
    });

    it('origem sem lead não aparece — e a API nunca a manda', () => {
      // `GROUP BY` só produz linha para o que existe, então zero nunca chega. O teste fixa o
      // contrato: se alguém passar a mandar zeros, a legenda não pode exibi-los.
      const fixture = montar([funilDe(1, 'A', 10)], [fatiaDe('site', 4, 80), fatiaDe('google', 1, 20)]);

      const valores = [...fixture.nativeElement.querySelectorAll('.legenda .legenda-valor')]
        .map(e => (e as Element).textContent!.trim());

      expect(valores.length).toBe(2);
      expect(valores.some(v => v.startsWith('0 '))).toBeFalse();
    });
  });

  // =========================================================================================
  describe('o estado vazio não manda conectar o que já está conectado', () => {
    /** ===================== O BUG QUE ISTO FIXA =====================
     *  `empresaSemDados` responde "ninguém no funil", e funil vazio acontece dos DOIS lados do
     *  onboarding: antes de conectar e depois, enquanto a primeira mensagem não chega. A tela
     *  tratava os dois como um só e mandava conectar um número que já estava no ar.
     *
     *  O teste vale porque o modo de falha é invisível para quem revisa: com o funil populado —
     *  que é o caso de todo dado de teste — este ramo nem renderiza.
     *  ============================================================== */
    /** ⚠️ OS DOIS SINAIS DESLIGADOS SÃO O QUE FAZ ESTE RAMO RENDERIZAR (POS-1). Antes bastava o
     *  funil zerado; hoje o quadro vazio já não significa empresa nova, e sem estas duas linhas os
     *  três testes abaixo nem chegariam no aviso — passariam a medir a página cheia. */
    function montarVazio(status: { whatsappConectado: boolean } | null) {
      if (status) TestBed.inject(PainelServico).ultimo.set(status as StatusPainel);
      return montar([funilDe(1, 'Novo Lead', 0)], [],
                    { recebeuMensagem: false, temContato: false });
    }

    /** A empresa do defeito que o POS-1 conserta: vendeu, concluiu, e por isso o quadro está
     *  vazio. Conectada e com a primeira mensagem recebida há muito tempo. */
    function montarQuemJaVendeu() {
      TestBed.inject(PainelServico).ultimo.set(
        { whatsappConectado: true } as StatusPainel);
      return montar([funilDe(1, 'Novo Lead', 0)], [],
                    { recebeuMensagem: true, temContato: true });
    }

    function texto(f: ComponentFixture<Dashboard>) {
      return (f.nativeElement.querySelector('.vazio') as HTMLElement).textContent!;
    }

    function temBotaoConectar(f: ComponentFixture<Dashboard>) {
      return [...f.nativeElement.querySelectorAll('.vazio a')]
        .some(a => (a as Element).textContent!.includes('Conectar meu WhatsApp'));
    }

    it('CONECTADO: pede a primeira mensagem, e NÃO oferece conectar de novo', () => {
      const fixture = montarVazio({ whatsappConectado: true });

      expect(temBotaoConectar(fixture)).toBeFalse();
      expect(texto(fixture)).toContain('Falta a primeira mensagem');
    });

    it('DESCONECTADO: pede para conectar', () => {
      const fixture = montarVazio({ whatsappConectado: false });

      expect(temBotaoConectar(fixture)).toBeTrue();
      expect(texto(fixture)).toContain('Conecte seu WhatsApp');
    });

    it('EMPRESA QUE JÁ VENDEU E ESTÁ COM O QUADRO VAZIO vê os números, não o onboarding', () => {
      // ===================== O DEFEITO, EXATAMENTE COMO APARECEU =====================
      // 1 contato, 2 vendas concluídas, 1 mensagem recebida, WhatsApp conectado. Card concluído
      // sai do quadro (`RegrasNegociacao.NoQuadro`), então o funil volta zerado — e a tela
      // respondia "empresa nova" a partir disso, com um aviso que OCUPA A PÁGINA e escondia o
      // faturamento do mês atrás dele.
      //
      // As duas afirmações são o teste: o aviso não está lá, E os números estão. Só a primeira
      // deixaria passar a tela em branco.
      // ==============================================================================
      const fixture = montarQuemJaVendeu();

      expect(fixture.nativeElement.querySelector('.vazio')).toBeNull();

      const kpis = [...fixture.nativeElement.querySelectorAll('.kpi')]
        .map(k => (k as HTMLElement).textContent!);
      expect(kpis.some(t => t.includes('Faturamento'))).toBeTrue();
      expect(kpis.some(t => t.includes('Vendas do mês'))).toBeTrue();
    });

    it('o funil vazio de quem já vendeu diz "nenhum contato em negociação", sem mandar conectar', () => {
      // O quadro vazio continua merecendo uma frase — mas uma LINHA dentro do widget do funil, não
      // um aviso no lugar da página. Parado é um número (zero), não um estado do produto.
      const fixture = montarQuemJaVendeu();

      const corpo = (fixture.nativeElement as HTMLElement).textContent!;
      expect(corpo).toContain('Nenhum contato em negociação');
      expect(corpo).not.toContain('Conectar meu WhatsApp');
      expect(corpo).not.toContain('Falta a primeira mensagem');
    });

    it('SEM STATUS AINDA: não afirma nenhum dos dois', () => {
      // Antes da primeira resposta do `/painel/status` não dá para saber. Afirmar cedo demais é
      // justamente como o bug aparecia — e um botão "Conectar" aqui reintroduziria o mesmo erro
      // por meio segundo, que é tempo de sobra para alguém clicar.
      const fixture = montarVazio(null);

      expect(temBotaoConectar(fixture)).toBeFalse();
      expect(texto(fixture)).not.toContain('Conecte seu WhatsApp');
      expect(texto(fixture)).not.toContain('Falta a primeira mensagem');
    });
  });

  // ==================================================================== ritmo vertical
  /** ===================== A MARGEM ENTRE OS CARTÕES =====================
   *
   *  A linha do funil e da rosca não tinha margem inferior nenhuma, e o cartão da Evolução vinha
   *  colado nela — enquanto os blocos acima respiravam 12px. Só o de baixo destoava, e o olho lê
   *  isso como "o gráfico pertence ao funil", que não é verdade: são recortes diferentes
   *  (situação agora × evolução no período).
   *
   *  O teste mede o ESTILO COMPUTADO, não a folha: um `margin-bottom` sobrescrito por outra regra
   *  mais específica passaria por qualquer leitura do CSS e cairia aqui.
   *  ============================================================== */
  describe('ritmo vertical', () => {
    /** O `gap` das grades. A distância entre dois cartões é a mesma lado a lado e um embaixo do
     *  outro — espaçamento que muda de eixo faz a página parecer torta sem ninguém saber onde. */
    const RITMO = '12px';

    function margemDe(fixture: ComponentFixture<Dashboard>, seletor: string): string {
      const el = (fixture.nativeElement as HTMLElement).querySelector(seletor);
      expect(el).withContext(`o bloco ${seletor} sumiu da tela`).not.toBeNull();
      return getComputedStyle(el!).marginBottom;
    }

    it('todo bloco da página tem a MESMA margem embaixo', () => {
      const fixture = montar(
        [{ pipelineId: 1, nome: 'Vendas', cor: '#7FA88B', emNegociacao: 5, valorEmAberto: 500, ganhasNoMes: 0, conversaoPercentual: null }],
        [fatiaDe('whatsapp', 7, 100)]);

      // A linha do funil/rosca é a que estava zerada — é ela que este teste existe para pegar.
      expect(margemDe(fixture, '.colunas')).withContext('funil e rosca').toBe(RITMO);
      expect(margemDe(fixture, '.numeros')).withContext('KPIs').toBe(RITMO);
      expect(margemDe(fixture, '.secundarios')).withContext('secundários').toBe(RITMO);
      expect(margemDe(fixture, '.grafico-cartao')).withContext('Evolução').toBe(RITMO);
    });

    it('o último bloco não empurra rodapé nenhum', () => {
      // COM dados: com o funil vazio a tela troca para o estado vazio, e `.colunas` nem existe —
      // o teste passaria a medir uma página que não é a que o cliente vê.
      const fixture = montar(
        [{ pipelineId: 1, nome: 'Vendas', cor: '#7FA88B', emNegociacao: 5, valorEmAberto: 500, ganhasNoMes: 0, conversaoPercentual: null }],
        [fatiaDe('whatsapp', 7, 100)]);

      const blocos = (fixture.nativeElement as HTMLElement).querySelectorAll('.colunas');
      expect(blocos.length).withContext('a página tem duas fileiras de colunas').toBe(2);
      expect(getComputedStyle(blocos[1]).marginBottom).toBe('0px');
    });
  });

  // ==================================================================== paginação
  /** ===================== OS DOIS CARTÕES DO RODAPÉ =====================
   *
   *  Eles não têm o mesmo problema, e por isso não têm a mesma solução:
   *
   *   ATIVIDADES  pagina no lugar. O feed NÃO tem tela de destino — "Abrir caixa" leva às
   *               conversas, que é outra coisa —, então sem "Carregar mais" o resto fica
   *               inalcançável.
   *
   *   TAREFAS     conta. O Meu Dia é exatamente esta lista e está a um clique; paginar aqui
   *               duplicaria aquela tela. O que faltava era dizer que há mais.
   *
   *  E o cartão de tarefas passou a pedir `limite=6` à API — antes pedia TUDO e descartava com
   *  `.slice(0, 6)`.
   *  ====================================================================== */
  describe('paginação dos cartões do rodapé', () => {
    function atividade(n: number) {
      return {
        tipo: 'venda', chave: `venda:${n}`, quando: `2026-08-0${n}T10:00:00Z`,
        contatoId: n, contatoNome: `Cliente ${n}`, titulo: `Venda ${n}`,
        detalhe: null, valor: 100, responsavelId: null, responsavelNome: null
      };
    }

    /** Como o `montar` do arquivo, mas com controle sobre o que cada rota devolve. */
    function montarCom(feed: { itens: unknown[]; temMais: boolean },
                       dia: { acoes: unknown[]; respondendo: number; lembretes: number; total: number }) {
      const fixture = TestBed.createComponent(Dashboard);
      fixture.detectChanges();

      for (const r of httpMock.match(() => true)) {
        const url = r.request.url;
        if (url.includes('/dashboard/atividades')) r.flush(feed);
        else if (url.includes('/meu-dia')) r.flush(dia);
        else if (url.includes('/dashboard/serie')) r.flush({ de: '', ate: '', agrupamento: 'dia', pontos: [] });
        else r.flush({
          leadsHoje: 3, aguardandoResposta: 2, followUpsPendentes: 1,
          vendasDoMes: 4, faturamentoDoMes: 1000, taxaConversaoPercentual: 50,
          funil: [{ pipelineId: 1, nome: 'Vendas', cor: '#7FA88B', emNegociacao: 5, valorEmAberto: 500, ganhasNoMes: 0, conversaoPercentual: null }],
          totalEmNegociacao: 5, totalValorEmAberto: 500, leadsTotal: 7,
          origens: [{ origem: 'whatsapp', agrupada: false, leads: 7, percentual: 100, campanhas: [] }],
          // POS-1 · empresa que ja opera. Sem estas duas linhas a tela cai no aviso de estreia e
          // o rodape que estes testes medem nem renderiza.
          recebeuMensagem: true, temContato: true
        });
      }
      fixture.detectChanges();
      return fixture;
    }

    const TAREFA = {
      tipo: 'responder', id: 1, contatoId: 9, contatoNome: 'Ana', contatoTelefone: '5584',
      titulo: 'Responder Ana', conversaId: 1, aguardandoDesde: null, minutosUteis: 10,
      esperaAcimaDaJanela: false, horaAlvo: null, dataAlvo: null, atrasado: false
    };

    // ============================================================ atividades
    it('"Carregar mais" manda o cursor do ÚLTIMO item e ACRESCENTA à lista', () => {
      const fixture = montarCom(
        { itens: [atividade(1), atividade(2)], temMais: true },
        { acoes: [], respondendo: 0, lembretes: 0, total: 0 });

      const raiz = fixture.nativeElement as HTMLElement;
      const botao = raiz.querySelector<HTMLButtonElement>('.carregar-mais')!;
      expect(botao).withContext('o botão aparece quando temMais').not.toBeNull();

      botao.click();

      const req = httpMock.expectOne(r => r.url.includes('/dashboard/atividades'));
      // O CURSOR é o par (quando, chave) do último — não offset. Com offset, um evento novo no
      // topo faria a segunda página repetir ou pular item.
      expect(req.request.params.get('cursorEm')).toBe(atividade(2).quando);
      expect(req.request.params.get('cursorChave')).toBe(atividade(2).chave);

      req.flush({ itens: [atividade(3)], temMais: false });
      fixture.detectChanges();

      // ⚠️ ACRESCENTA. Uma implementação que faça `feed.set(p.itens)` passaria em qualquer teste
      // que só conferisse a requisição — e o cartão "avançaria" em vez de crescer.
      expect(fixture.componentInstance.feed().map(a => a.chave))
        .toEqual(['venda:1', 'venda:2', 'venda:3']);

      // E o botão some quando acabou.
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('.carregar-mais')).toBeNull();
    });

    it('sem mais nada para carregar, o botão nem aparece', () => {
      const fixture = montarCom(
        { itens: [atividade(1)], temMais: false },
        { acoes: [], respondendo: 0, lembretes: 0, total: 0 });

      expect((fixture.nativeElement as HTMLElement).querySelector('.carregar-mais')).toBeNull();
    });

    // ============================================================ tarefas
    it('o cartão de tarefas pede limite=6 à API, em vez de baixar tudo', () => {
      TestBed.createComponent(Dashboard).detectChanges();

      const req = httpMock.match(r => r.url.includes('/meu-dia'));
      expect(req.length).toBe(1);
      // É este parâmetro que faz o corte acontecer no SQL. Sem ele, uma empresa com 300
      // conversas esperando baixa 300 para desenhar 6.
      expect(req[0].request.params.get('limite')).toBe('6');

      for (const r of httpMock.match(() => true)) r.flush({ acoes: [], respondendo: 0, lembretes: 0, total: 0 });
    });

    /** O 23 é o `total` do SERVIDOR (AUD-XX). Os dois contadores somam 22 de propósito: se a tela
     *  voltar a somar, ela mostra "1 de 22". */
    it('mostra "1 de 23" quando o total é maior que a lista, e NÃO pagina', () => {
      const fixture = montarCom(
        { itens: [], temMais: false },
        { acoes: [TAREFA], respondendo: 20, lembretes: 2, total: 23 });

      const raiz = fixture.nativeElement as HTMLElement;
      const rodape = raiz.querySelector('.rodape-lista')!;

      expect(rodape).withContext('o contador aparece quando há mais').not.toBeNull();
      expect(rodape.textContent).toContain('1 de 23');
      // A porta é o Meu Dia, não um botão de carregar mais.
      expect(rodape.querySelector('a')!.getAttribute('href')).toBe('/meu-dia');
    });

    it('quando cabe tudo, não há contador nenhum', () => {
      const fixture = montarCom(
        { itens: [], temMais: false },
        { acoes: [TAREFA], respondendo: 1, lembretes: 0, total: 1 });

      expect((fixture.nativeElement as HTMLElement).querySelector('.rodape-lista')).toBeNull();
    });
  });
});
