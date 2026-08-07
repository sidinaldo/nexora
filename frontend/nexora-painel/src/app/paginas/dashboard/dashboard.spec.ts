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
});
