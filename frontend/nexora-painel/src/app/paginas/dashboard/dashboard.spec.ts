import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { EtapaFunilDto, OrigemDto, StatusPainel } from '../../nucleo/modelos';
import { Dashboard } from './dashboard';

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
      usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel: 'dono', empresaNome: 'X' }
    } as never);
  });

  afterEach(() => localStorage.clear());

  function etapa(id: number, nome: string, contatos: number): EtapaFunilDto {
    return { etapaId: id, nome, ordem: id, cor: '#7FA88B', contatos, valor: 0 };
  }

  /** Monta a tela e responde as três chamadas do `ngOnInit`. */
  function montar(funil: EtapaFunilDto[], origens: OrigemDto[]): ComponentFixture<Dashboard> {
    const fixture = TestBed.createComponent(Dashboard);
    fixture.detectChanges();

    for (const r of httpMock.match(() => true)) {
      const url = r.request.url;
      if (url.includes('/dashboard/serie')) {
        r.flush({ de: '', ate: '', agrupamento: 'dia', pontos: [] });
      } else if (url.includes('/dashboard/atividades')) {
        r.flush({ itens: [], temMais: false });
      } else if (url.includes('/meu-dia')) {
        r.flush({ acoes: [], respondendo: 0, lembretes: 0 });
      } else {
        r.flush({
          leadsHoje: 3, aguardandoResposta: 2, followUpsPendentes: 1,
          vendasDoMes: 4, faturamentoDoMes: 1000, taxaConversao: 0.5,
          funil, origens
        });
      }
    }
    fixture.detectChanges();
    return fixture;
  }

  describe('o funil vem da API', () => {
    it('DESENHA A QUANTIDADE DE ETAPAS QUE A API DEVOLVER, não cinco fixas', () => {
      // ===================== POR QUE ISTO É TESTE =====================
      // As cinco etapas padrão são só o que o cadastro semeia — a empresa pode ter três ou oito.
      // Um `@for` sobre a resposta parece obviamente certo e continua certo por acidente enquanto
      // todo mundo tiver cinco; o dia em que alguém escrever as faixas no código, só um cliente
      // com funil diferente descobre.
      // ===============================================================
      for (const quantas of [3, 5, 8]) {
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
          usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel: 'dono', empresaNome: 'X' }
        } as never);

        const etapas = Array.from({ length: quantas },
          (_, i) => etapa(i + 1, `Etapa ${i + 1}`, 20 - i * 2));

        const fixture = montar(etapas, [{ origem: 'site', leads: 5 }]);
        const faixas = fixture.nativeElement.querySelectorAll('.funil-desenho .faixa-linha');

        expect(faixas.length).withContext(`${quantas} etapas na API`).toBe(quantas);
      }
    });

    it('cada faixa leva ao quadro FILTRADO por aquela etapa', () => {
      const fixture = montar(
        [etapa(7, 'Novo Lead', 10), etapa(9, 'Proposta', 4)],
        [{ origem: 'site', leads: 5 }]);

      const links = fixture.nativeElement.querySelectorAll('.funil-desenho .faixa-linha');
      expect((links[0] as HTMLAnchorElement).getAttribute('href')).toContain('etapa=7');
      expect((links[1] as HTMLAnchorElement).getAttribute('href')).toContain('etapa=9');
    });

    it('o degradê escurece do topo para a base', () => {
      const fixture = montar(
        [etapa(1, 'A', 10), etapa(2, 'B', 8), etapa(3, 'C', 5)],
        [{ origem: 'site', leads: 5 }]);

      // Soma dos canais RGB: quanto mais escuro, menor. Cada faixa tem que ser mais escura que a
      // anterior — é isso que faz a figura ler como funil e não como três barras soltas.
      const somas = [...fixture.nativeElement.querySelectorAll('.funil-desenho .faixa')]
        .map(el => (getComputedStyle(el as Element).backgroundColor.match(/\d+/g) ?? [])
          .slice(0, 3).reduce((s: number, c: string) => s + Number(c), 0));

      expect(somas[0]).toBeGreaterThan(somas[1]);
      expect(somas[1]).toBeGreaterThan(somas[2]);
    });
  });

  describe('a rosca de origens', () => {
    const noveOrigens: OrigemDto[] = [
      { origem: 'instagram', leads: 15 }, { origem: 'whatsapp', leads: 13 },
      { origem: 'indicacao', leads: 10 }, { origem: 'google', leads: 7 },
      { origem: 'site', leads: 5 }, { origem: 'facebook', leads: 4 },
      { origem: 'qrcode', leads: 3 }, { origem: 'manual', leads: 2 },
      { origem: 'outro', leads: 1 }
    ];

    it('SÓ TONS DE VERDE (mais o creme do "Outros") — nada de azul, vermelho ou laranja', () => {
      // ===================== A RESTRIÇÃO DE PALETA =====================
      // Verde, creme e UM tom de alerta. A exceção acordada são os três estados do semáforo,
      // onde a cor É a informação. Numa rosca a cor é só rótulo — sair da paleta por comodidade
      // é como se perde a identidade de um produto, um gráfico de cada vez.
      // ================================================================
      const fixture = montar([etapa(1, 'A', 10)], noveOrigens);
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

    it('agrupa o excedente em "Outros" em vez de listar nove fatias', () => {
      // Sete verdes seguidos deixam de ser distinguíveis, e legenda que ninguém consegue casar
      // com a fatia não informa nada.
      const fixture = montar([etapa(1, 'A', 10)], noveOrigens);
      const nomes = [...fixture.nativeElement.querySelectorAll('.legenda .legenda-nome')]
        .map(e => (e as Element).textContent!.trim());

      expect(nomes.length).toBe(6);
      expect(nomes[nomes.length - 1]).toBe('Outros');
    });

    it('OS PERCENTUAIS SOMAM 100%', () => {
      // Arredondar cada fatia por conta própria dá 99% ou 101% na legenda — o clássico "os
      // números não fecham" que faz o dono desconfiar do resto da tela.
      for (const origens of [
        noveOrigens,
        // Três terços: o caso que mais quebra arredondamento (33+33+33 = 99).
        [{ origem: 'site', leads: 1 }, { origem: 'google', leads: 1 },
         { origem: 'manual', leads: 1 }] as OrigemDto[]
      ]) {
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
          usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel: 'dono', empresaNome: 'X' }
        } as never);

        const fixture = montar([etapa(1, 'A', 10)], origens);
        const soma = [...fixture.nativeElement.querySelectorAll('.legenda .legenda-valor')]
          .map(e => Number((e as Element).textContent!.match(/(\d+)%/)![1]))
          .reduce((s, p) => s + p, 0);

        expect(soma).withContext(`${origens.length} origens`).toBe(100);
      }
    });

    it('origem sem lead não aparece — e a API nunca a manda', () => {
      // `GROUP BY` só produz linha para o que existe, então zero nunca chega. O teste fixa o
      // contrato: se alguém passar a mandar zeros, a legenda não pode exibi-los.
      const fixture = montar([etapa(1, 'A', 10)],
        [{ origem: 'site', leads: 4 }, { origem: 'google', leads: 1 }]);

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
    function montarVazio(status: { whatsappConectado: boolean } | null) {
      if (status) TestBed.inject(PainelServico).ultimo.set(status as StatusPainel);
      return montar([etapa(1, 'Novo Lead', 0)], []);
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
        [{ etapaId: 1, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', contatos: 5, valor: 500 }],
        [{ origem: 'whatsapp', leads: 7 }]);

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
        [{ etapaId: 1, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', contatos: 5, valor: 500 }],
        [{ origem: 'whatsapp', leads: 7 }]);

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
                       dia: { acoes: unknown[]; respondendo: number; lembretes: number }) {
      const fixture = TestBed.createComponent(Dashboard);
      fixture.detectChanges();

      for (const r of httpMock.match(() => true)) {
        const url = r.request.url;
        if (url.includes('/dashboard/atividades')) r.flush(feed);
        else if (url.includes('/meu-dia')) r.flush(dia);
        else if (url.includes('/dashboard/serie')) r.flush({ de: '', ate: '', agrupamento: 'dia', pontos: [] });
        else r.flush({
          leadsHoje: 3, aguardandoResposta: 2, followUpsPendentes: 1,
          vendasDoMes: 4, faturamentoDoMes: 1000, taxaConversao: 0.5,
          funil: [{ etapaId: 1, nome: 'Novo Lead', ordem: 1, cor: '#7FA88B', contatos: 5, valor: 500 }],
          origens: [{ origem: 'whatsapp', leads: 7 }]
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
        { acoes: [], respondendo: 0, lembretes: 0 });

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
        { acoes: [], respondendo: 0, lembretes: 0 });

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

      for (const r of httpMock.match(() => true)) r.flush({ acoes: [], respondendo: 0, lembretes: 0 });
    });

    it('mostra "1 de 23" quando o total é maior que a lista, e NÃO pagina', () => {
      const fixture = montarCom(
        { itens: [], temMais: false },
        { acoes: [TAREFA], respondendo: 20, lembretes: 3 });

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
        { acoes: [TAREFA], respondendo: 1, lembretes: 0 });

      expect((fixture.nativeElement as HTMLElement).querySelector('.rodape-lista')).toBeNull();
    });
  });
});
