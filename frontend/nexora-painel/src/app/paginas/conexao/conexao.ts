import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ConexaoServico } from '../../nucleo/servicos/conexao.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { Conexao as ConexaoModel, QrCode, SaudeConexao } from '../../nucleo/modelos';

/** O número de WhatsApp da empresa. UMA conexão por empresa na fase 1 — não há CRUD, só
 *  pareamento e status. */
@Component({
  selector: 'app-conexao',
  imports: [FormsModule, DatePipe],
  templateUrl: './conexao.html',
  styleUrl: './conexao.css'
})
export class Conexao implements OnInit, OnDestroy {
  private servico = inject(ConexaoServico);
  private toast = inject(ToastServico);

  conexao = signal<ConexaoModel | null>(null);
  saude = signal<SaudeConexao | null>(null);
  qr = signal<QrCode | null>(null);

  /** Estado cru da Evolution. `offline` é distinto de `desconectado`: offline = a Evolution
   *  não respondeu (problema da infraestrutura, o usuário não tem o que fazer). */
  estado = signal<string>('');
  conectado = signal(false);

  carregando = signal(true);
  gerandoQr = signal(false);
  erro = signal('');

  numeroPareamento = signal('');
  modoPareamento = signal(false);

  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit() {
    this.carregar();
    // Polling de 3s: é assim que a tela descobre que o QR foi lido. O webhook
    // connection.update também chega, mas não dá para depender só dele com o QR na frente
    // do usuário.
    this.timer = setInterval(() => this.verificarStatus(), 3000);
  }

  ngOnDestroy() {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
  }

  private carregar() {
    this.servico.minha().subscribe({
      next: c => { this.conexao.set(c); this.carregando.set(false); this.verificarStatus(); },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar a conexão.');
        this.carregando.set(false);
      }
    });
    this.servico.saude().subscribe({ next: s => this.saude.set(s), error: () => { } });
  }

  private verificarStatus() {
    this.servico.status().subscribe({
      next: s => {
        const acabouDeConectar = s.conectado && !this.conectado();
        this.estado.set(s.estado);
        this.conectado.set(s.conectado);

        if (acabouDeConectar) {
          // Conectou: o QR não serve mais, e o número/perfil chegam pelo webhook.
          this.qr.set(null);
          this.toast.sucesso('WhatsApp conectado.');
          this.servico.minha().subscribe(c => this.conexao.set(c));
        }
      },
      error: () => { }   // o polling não pode encher a tela de erro
    });
  }

  gerarQr() {
    this.gerandoQr.set(true);
    this.erro.set('');
    this.modoPareamento.set(false);
    this.servico.conectar().subscribe({
      next: q => {
        this.qr.set(q);
        this.gerandoQr.set(false);
        if (q.conectado) this.toast.info('Este número já está conectado.');
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível gerar o QR code.');
        this.gerandoQr.set(false);
      }
    });
  }

  parear() {
    this.gerandoQr.set(true);
    this.erro.set('');
    this.servico.parear(this.numeroPareamento()).subscribe({
      next: q => {
        this.qr.set(q);
        this.gerandoQr.set(false);
        if (!q.pairingCode) {
          // Verificado na v2.3.7: nem toda versão da Evolution devolve o código. Melhor dizer
          // isso do que deixar o usuário esperando um número que não vem.
          this.toast.info('Esta versão da Evolution não devolveu o código. Use o QR code.');
        }
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível gerar o código.');
        this.gerandoQr.set(false);
      }
    });
  }

  desconectar() {
    if (!confirm('Desconectar o WhatsApp? As mensagens param de ser enviadas e recebidas.')) return;
    this.servico.desconectar().subscribe({
      next: () => { this.conectado.set(false); this.qr.set(null); this.carregar(); },
      error: e => this.erro.set(e.error?.erro ?? 'Não foi possível desconectar.')
    });
  }

  reconhecerTroca() {
    this.servico.reconhecerTroca().subscribe(() => this.carregar());
  }

  /** Rótulo honesto do estado: `offline` não pede ação do usuário. */
  rotuloEstado(): string {
    switch (this.estado()) {
      case 'open': return 'Conectado';
      case 'connecting': return 'Aguardando leitura do QR code';
      case 'close': return 'Desconectado';
      case 'nao_criada': return 'Ainda não conectado';
      case 'offline': return 'Serviço de WhatsApp indisponível';
      default: return '—';
    }
  }
}
