import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HubConnection } from '@microsoft/signalr';
import { AuthServico } from './auth.servico';
import { AGENDADOR, CANCELADOR, FABRICA_HUB, RealtimeServico } from './realtime.servico';
import { PoliticaReconexao, esperaDaTentativa } from './reconexao';

/** ===================== O TEMPO REAL PRECISA INSISTIR =====================
 *
 *  O defeito que motivou este arquivo: com o painel aberto, mensagem nova não aparecia — só
 *  trocando de tela e voltando. O hub estava certo (verificado conectando nele por fora, e os
 *  eventos chegavam na hora) e os handlers também. O que estava morto era a CONEXÃO, e nada a
 *  religava.
 *
 *  Nada disso QUEBRA quando falha: o painel continua funcionando por requisição normal. É
 *  justamente por isso que precisa de teste — o sintoma é a tela parar de se mexer, e ninguém
 *  associa isso a uma conexão.
 *
 *  ⚠️ SEM MOCK DE RELÓGIO. `fakeAsync` não existe aqui (o projeto é zoneless) e
 *  `jasmine.clock()` trava o Karma, que usa o `setTimeout` real para o próprio heartbeat — o
 *  navegador desconecta com "no message in 30000 ms". Por isso o AGENDADOR é um seam: o teste
 *  guarda o callback e o dispara na mão.
 *  ====================================================================== */
describe('RealtimeServico — reconexão', () => {
  /** Conexão falsa: o teste decide se o `start()` dá certo. */
  class HubFalso {
    static criadas: HubFalso[] = [];
    static falharNoStart = 0;

    aoFechar: (() => void) | null = null;
    parou = false;

    on() { /* os handlers não interessam a estes testes */ }
    onclose(h: () => void) { this.aoFechar = h; }
    onreconnected() { /* idem */ }
    stop() { this.parou = true; return Promise.resolve(); }

    start() {
      if (HubFalso.falharNoStart > 0) {
        HubFalso.falharNoStart--;
        return Promise.reject(new Error('servidor fora do ar'));
      }
      return Promise.resolve();
    }
  }

  /** As tentativas marcadas e ainda não disparadas. */
  let marcadas: { fn: () => void; ms: number; cancelada: boolean }[] = [];

  let servico: RealtimeServico;
  let auth: AuthServico;

  /** Dispara a próxima tentativa pendente e deixa a cadeia async assentar. Devolve a espera que
   *  tinha sido pedida — é como os testes conferem o escalonamento sem relógio. */
  async function dispararPendente(): Promise<number> {
    const alvo = marcadas.find(m => !m.cancelada);
    expect(alvo).withContext('nenhuma tentativa foi marcada').toBeDefined();

    alvo!.cancelada = true;
    alvo!.fn();
    await Promise.resolve();
    await Promise.resolve();
    return alvo!.ms;
  }

  const pendentes = () => marcadas.filter(m => !m.cancelada).length;

  beforeEach(() => {
    marcadas = [];
    HubFalso.criadas = [];
    HubFalso.falharNoStart = 0;

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: FABRICA_HUB,
          useValue: () => {
            const c = new HubFalso();
            HubFalso.criadas.push(c);
            return c as unknown as HubConnection;
          }
        },
        {
          provide: AGENDADOR,
          useValue: (fn: () => void, ms: number) => {
            const m = { fn, ms, cancelada: false };
            marcadas.push(m);
            return m;
          }
        },
        {
          provide: CANCELADOR,
          useValue: (id: unknown) => { (id as { cancelada: boolean }).cancelada = true; }
        }
      ]
    });

    auth = TestBed.inject(AuthServico);
    auth.aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'X' }
    } as never);

    servico = TestBed.inject(RealtimeServico);
  });

  afterEach(() => localStorage.clear());

  it('conecta e marca conectado', async () => {
    await servico.conectar();

    expect(HubFalso.criadas.length).toBe(1);
    expect(servico.conectado()).toBeTrue();
    expect(pendentes()).withContext('conectou: nada a remarcar').toBe(0);
  });

  /** ===================== O DEFEITO, EM UM TESTE =====================
   *  A versão anterior fazia `this.conexao = undefined` no catch e ia embora. Uma falha — a API
   *  ainda subindo, a rede piscando — e o tempo real morria pelo resto da sessão, calado.
   *  ============================================================== */
  it('O PRIMEIRO start QUE FALHA NAO E O FIM: tenta de novo sozinho', async () => {
    HubFalso.falharNoStart = 2;

    await servico.conectar();
    expect(servico.conectado()).withContext('falhou, como o teste mandou').toBeFalse();
    expect(pendentes()).withContext('marcou a próxima em vez de desistir').toBe(1);

    await dispararPendente();
    expect(servico.conectado()).withContext('a segunda também falha').toBeFalse();

    await dispararPendente();

    expect(servico.conectado()).withContext('a terceira pegou').toBeTrue();
    expect(HubFalso.criadas.length).toBe(3);
  });

  it('a espera CRESCE entre as tentativas, em vez de martelar', async () => {
    HubFalso.falharNoStart = 4;
    await servico.conectar();

    const esperas = [await dispararPendente(), await dispararPendente(), await dispararPendente()];

    // A primeira é imediata (restart de API costuma voltar em segundos); daí sobe.
    expect(esperas[0]).toBe(0);
    expect(esperas[1]).toBeGreaterThan(esperas[0]);
    expect(esperas[2]).toBeGreaterThan(esperas[1]);
  });

  it('conexao que CAI de vez volta a ser tentada', async () => {
    await servico.conectar();
    expect(servico.conectado()).toBeTrue();

    // `onclose` só dispara quando a política de reconexão desistiu — e é aí que a insistência do
    // serviço recomeça.
    HubFalso.criadas[0].aoFechar!();
    expect(servico.conectado()).toBeFalse();

    await dispararPendente();

    expect(servico.conectado()).withContext('religou sozinha').toBeTrue();
    expect(HubFalso.criadas.length).toBe(2);
  });

  /** Sem token, o shell pode montar antes de o login gravar. Antes saía um `return` seco e a
   *  conexão nunca acontecia — nem depois de o token chegar. */
  it('SEM TOKEN ainda, espera e tenta de novo', async () => {
    auth.limpar();

    await servico.conectar();
    expect(HubFalso.criadas.length).withContext('nem tentou, e está certo').toBe(0);
    expect(pendentes()).withContext('mas marcou para tentar de novo').toBe(1);

    auth.aplicarLogin({
      token: 'chegou',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'X' }
    } as never);

    await dispararPendente();

    expect(servico.conectado()).withContext('assim que o token chegou').toBeTrue();
  });

  /** A aba voltando ao foco é o momento em que o vendedor está olhando — esperar o backoff ali
   *  seria esperar por nada. */
  it('a aba voltando ao foco tenta NA HORA, sem esperar a espera marcada', async () => {
    HubFalso.falharNoStart = 1;

    await servico.conectar();
    expect(servico.conectado()).toBeFalse();

    document.dispatchEvent(new Event('visibilitychange'));
    await Promise.resolve();
    await Promise.resolve();

    // Sem disparar tentativa marcada nenhuma.
    expect(servico.conectado()).toBeTrue();
  });

  it('desconectar CANCELA a tentativa pendente', async () => {
    HubFalso.falharNoStart = 5;

    await servico.conectar();
    expect(pendentes()).toBe(1);

    await servico.desconectar();

    expect(pendentes()).withContext('o logout não pode deixar timer religando').toBe(0);
    expect(servico.conectado()).toBeFalse();
  });

  // ==================================================================== a política
  describe('PoliticaReconexao', () => {
    /** ⚠️ O TESTE MAIS IMPORTANTE DO ARQUIVO. A política padrão do SignalR devolve `null` na
     *  quinta tentativa e o SignalR PARA. Quarenta segundos é menos que um restart de API. */
    it('NUNCA DESISTE — nem na tentativa mil', () => {
      const p = new PoliticaReconexao();

      for (const n of [0, 1, 5, 10, 100, 1000]) {
        const espera = p.nextRetryDelayInMilliseconds(
          { previousRetryCount: n, elapsedMilliseconds: 0, retryReason: new Error() });

        expect(espera)
          .withContext(`tentativa ${n} devolveu ${espera} — nulo faz o SignalR desistir`)
          .not.toBeNull();
        expect(espera).toBeLessThanOrEqual(PoliticaReconexao.TETO);
      }
    });

    it('começa rápido e cresce até o teto', () => {
      expect(esperaDaTentativa(0)).withContext('a primeira é imediata').toBe(0);
      expect(esperaDaTentativa(1)).toBeGreaterThan(0);
      expect(esperaDaTentativa(1)).toBeLessThan(esperaDaTentativa(4));
      expect(esperaDaTentativa(50)).toBe(PoliticaReconexao.TETO);
    });
  });
});
