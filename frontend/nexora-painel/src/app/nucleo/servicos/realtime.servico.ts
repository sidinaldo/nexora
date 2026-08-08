import { InjectionToken, Injectable, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { HUB } from '../api-base';
import {
  ConexaoPainel, ContatoPainel, ConversaPainel, MensagemPainel, StatusMensagemPainel
} from '../modelos';
import { AuthServico } from './auth.servico';
import { PoliticaReconexao, esperaDaTentativa } from './reconexao';

/** Como uma tentativa futura é marcada. Existe como token pelo mesmo motivo do `FABRICA_HUB`:
 *  sem ele, testar a reconexão exigiria mockar o `setTimeout` global — e mockar o relógio no
 *  Karma trava o próprio Karma, que também usa `setTimeout` para o heartbeat.
 *
 *  Com o seam, o teste guarda o callback e o dispara quando quiser. Sem relógio, sem espera. */
export const AGENDADOR = new InjectionToken<(fn: () => void, ms: number) => unknown>(
  'AGENDADOR',
  { providedIn: 'root', factory: () => (fn, ms) => setTimeout(fn, ms) });

/** Como a tentativa marcada é cancelada. Par do `AGENDADOR`. */
export const CANCELADOR = new InjectionToken<(id: unknown) => void>(
  'CANCELADOR',
  { providedIn: 'root', factory: () => id => clearTimeout(id as ReturnType<typeof setTimeout>) });

/** Como a conexão é criada. Existe como token para o teste poder trocar por uma falsa — sem
 *  isso, provar que a reconexão funciona exigiria um servidor SignalR de verdade no karma. */
export const FABRICA_HUB = new InjectionToken<(token: () => string) => HubConnection>(
  'FABRICA_HUB',
  {
    providedIn: 'root',
    factory: () => (token: () => string) => new HubConnectionBuilder()
      .withUrl(HUB + '/painel', { accessTokenFactory: token })
      // ⚠️ COM POLÍTICA. Sem argumento, o SignalR desiste depois de ~40s — ver PoliticaReconexao.
      .withAutomaticReconnect(new PoliticaReconexao())
      .configureLogging(LogLevel.Warning)
      .build()
  });

/** Realtime do painel (SignalR).
 *
 *  A API põe cada conexão no grupo `empresa-{id}` lendo o claim do JWT — o mesmo isolamento
 *  multi-tenant do resto do sistema. Aqui só escutamos; responder é um POST normal.
 *
 *  O token vai na QUERY STRING, não no header: o WebSocket do navegador não permite header
 *  `Authorization`. A API o resgata de `access_token` (ver JwtBearerEvents no Program.cs).
 *
 *  ===================== POR QUE A RECONEXÃO É TÃO INSISTENTE =====================
 *  O defeito que motivou isto: com o painel aberto, mensagem nova não aparecia — só trocando de
 *  tela e voltando. O hub estava certo e os handlers também; o que estava morto era a CONEXÃO.
 *
 *  Duas maneiras de morrer, as duas em silêncio:
 *
 *    1. o primeiro `start()` falha (a API ainda não subiu, a rede piscou) e ninguém tenta de
 *       novo — `withAutomaticReconnect` só cobre conexão que chegou a subir;
 *    2. a conexão cai e a política padrão desiste depois de ~40s, que é menos que um restart
 *       de API.
 *
 *  Nos dois casos o painel continua funcionando por requisição normal, e é justamente isso que
 *  esconde o problema: nada quebra, só para de se mexer.
 *  ============================================================================== */
@Injectable({ providedIn: 'root' })
export class RealtimeServico {
  private auth = inject(AuthServico);
  private fabrica = inject(FABRICA_HUB);
  private agendador = inject(AGENDADOR);
  private cancelador = inject(CANCELADOR);

  private conexao?: HubConnection;
  private timer: unknown = null;
  private tentativa = 0;
  /** Desligado de propósito por `desconectar()` (logout). Sem isto, o timer pendente religaria
   *  a conexão de um usuário que acabou de sair. */
  private desligado = false;

  readonly conectado = signal(false);

  readonly mensagemRecebida$ = new Subject<MensagemPainel>();
  readonly conversaAberta$ = new Subject<ConversaPainel>();
  readonly contatoCriado$ = new Subject<ContatoPainel>();
  readonly statusMensagem$ = new Subject<StatusMensagemPainel>();
  readonly conexaoMudou$ = new Subject<ConexaoPainel>();

  constructor() {
    // A aba voltando ao foco e a rede voltando são os dois momentos em que vale tentar NA HORA,
    // sem esperar o backoff. É o que faz o painel "acordar junto" com quem está olhando para ele.
    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') this.agoraSePuder();
      });
    }
    if (typeof window !== 'undefined') {
      window.addEventListener('online', () => this.agoraSePuder());
    }
  }

  async conectar(): Promise<void> {
    this.desligado = false;
    if (this.conexao) return;

    // SEM TOKEN ainda não é motivo para desistir para sempre: o shell pode montar antes de o
    // login terminar de gravar. Antes daqui saía um `return` seco e a conexão nunca acontecia.
    if (!this.auth.token) return this.agendar();

    const conexao = this.fabrica(() => this.auth.token ?? '');

    conexao.on('mensagemRecebida', (m: MensagemPainel) => this.mensagemRecebida$.next(m));
    conexao.on('conversaAberta', (c: ConversaPainel) => this.conversaAberta$.next(c));
    conexao.on('contatoCriado', (c: ContatoPainel) => this.contatoCriado$.next(c));
    conexao.on('statusMensagem', (s: StatusMensagemPainel) => this.statusMensagem$.next(s));
    conexao.on('conexaoMudou', (c: ConexaoPainel) => this.conexaoMudou$.next(c));

    conexao.onreconnected(() => { this.tentativa = 0; this.conectado.set(true); });

    // `onclose` só dispara quando a política de reconexão desistiu — e a nossa não desiste. Fica
    // como rede de segurança para o fechamento definitivo (servidor recusando o token, por
    // exemplo), e aí é aqui que a insistência recomeça.
    conexao.onclose(() => {
      this.conectado.set(false);
      this.conexao = undefined;
      this.agendar();
    });

    this.conexao = conexao;

    try {
      await conexao.start();
      this.tentativa = 0;
      this.conectado.set(true);
    } catch {
      // Falhar aqui NÃO pode quebrar a tela: sem realtime o painel continua funcionando por
      // requisição normal. Mas também não pode ser o fim — antes era, e o tempo real morria
      // calado pelo resto da sessão.
      this.conectado.set(false);
      this.conexao = undefined;
      this.agendar();
    }
  }

  async desconectar(): Promise<void> {
    this.desligado = true;
    this.cancelar();
    await this.conexao?.stop();
    this.conexao = undefined;
    this.conectado.set(false);
  }

  /** A próxima tentativa, com o mesmo escalonamento da reconexão automática. */
  private agendar() {
    if (this.desligado || this.timer) return;

    const espera = esperaDaTentativa(this.tentativa);
    this.tentativa++;

    this.timer = this.agendador(() => {
      this.timer = null;
      void this.conectar();
    }, espera);
  }

  /** Tenta imediatamente, descartando a espera pendente. Chamado quando a aba volta ao foco ou
   *  a rede volta: são eventos que dizem "o mundo mudou", e esperar 30s depois deles seria
   *  esperar por nada. */
  private agoraSePuder() {
    if (this.desligado || this.conexao) return;
    this.cancelar();
    this.tentativa = 0;
    void this.conectar();
  }

  private cancelar() {
    if (this.timer !== null) this.cancelador(this.timer);
    this.timer = null;
  }
}
