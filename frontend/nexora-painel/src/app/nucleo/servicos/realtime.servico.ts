import { Injectable, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { HUB } from '../api-base';
import {
  ConexaoPainel, ContatoPainel, ConversaPainel, MensagemPainel, StatusMensagemPainel
} from '../modelos';
import { AuthServico } from './auth.servico';

/** Realtime do painel (SignalR).
 *
 *  A API põe cada conexão no grupo `empresa-{id}` lendo o claim do JWT — o mesmo isolamento
 *  multi-tenant do resto do sistema. Aqui só escutamos; responder é um POST normal.
 *
 *  O token vai na QUERY STRING, não no header: o WebSocket do navegador não permite header
 *  `Authorization`. A API o resgata de `access_token` (ver JwtBearerEvents no Program.cs). */
@Injectable({ providedIn: 'root' })
export class RealtimeServico {
  private auth = inject(AuthServico);
  private conexao?: HubConnection;

  readonly conectado = signal(false);

  readonly mensagemRecebida$ = new Subject<MensagemPainel>();
  readonly conversaAberta$ = new Subject<ConversaPainel>();
  readonly contatoCriado$ = new Subject<ContatoPainel>();
  readonly statusMensagem$ = new Subject<StatusMensagemPainel>();
  readonly conexaoMudou$ = new Subject<ConexaoPainel>();

  async conectar(): Promise<void> {
    if (this.conexao || !this.auth.token) return;

    this.conexao = new HubConnectionBuilder()
      .withUrl(HUB + '/painel', { accessTokenFactory: () => this.auth.token ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    this.conexao.on('mensagemRecebida', (m: MensagemPainel) => this.mensagemRecebida$.next(m));
    this.conexao.on('conversaAberta', (c: ConversaPainel) => this.conversaAberta$.next(c));
    this.conexao.on('contatoCriado', (c: ContatoPainel) => this.contatoCriado$.next(c));
    this.conexao.on('statusMensagem', (s: StatusMensagemPainel) => this.statusMensagem$.next(s));
    this.conexao.on('conexaoMudou', (c: ConexaoPainel) => this.conexaoMudou$.next(c));

    this.conexao.onreconnected(() => this.conectado.set(true));
    this.conexao.onclose(() => this.conectado.set(false));

    try {
      await this.conexao.start();
      this.conectado.set(true);
    } catch {
      // Falhar aqui NÃO pode quebrar a tela: sem realtime o painel continua funcionando por
      // requisição normal, só não atualiza sozinho. O indicador na sidebar mostra que caiu.
      this.conectado.set(false);
      this.conexao = undefined;
    }
  }

  async desconectar(): Promise<void> {
    await this.conexao?.stop();
    this.conexao = undefined;
    this.conectado.set(false);
  }
}
