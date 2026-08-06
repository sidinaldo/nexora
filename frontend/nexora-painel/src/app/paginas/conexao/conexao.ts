import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ConexaoServico } from '../../nucleo/servicos/conexao.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { Conexao as ConexaoModel, QrCode, SaudeConexao } from '../../nucleo/modelos';

/** OS NÚMEROS DE WHATSAPP DA EMPRESA.
 *
 *  ===================== O QUE MUDOU NO ARQ-2 =====================
 *  Era uma conexão só, sem CRUD. Virou lista: criar, renomear, apagar, e o pareamento por número.
 *
 *  Quantos números a empresa pode ter vem do PLANO, e quem responde é o servidor (`limite` /
 *  `podeAdicionar`). A tela não recalcula isso: um limite adivinhado aqui divergiria do aplicado
 *  lá no dia em que o contrato mudar, e o sintoma seria um botão que devolve erro.
 *
 *  O mesmo vale para `podeRemover` / `motivoNaoRemove`: só o banco sabe se há conversa apontando
 *  para a conexão.
 *
 *  ===================== POR QUE O POLLING ENCOLHEU =====================
 *  Antes a tela consultava o estado ao vivo a cada 3s, sempre. Com N números isso viraria N
 *  requisições por tick — e a Evolution responde uma por instância. O poll agora existe só
 *  enquanto o QR de UMA conexão está na frente do usuário, que é a única situação em que 3s se
 *  justificam: é assim que a tela descobre que o pareamento deu certo. Fora disso, o status vem
 *  do banco (webhook + última consulta), que é o mesmo que o resto do painel usa.
 *  ================================================================== */
@Component({
  selector: 'app-conexao',
  imports: [FormsModule, DatePipe],
  templateUrl: './conexao.html',
  styleUrl: './conexao.css'
})
export class Conexao implements OnInit, OnDestroy {
  private servico = inject(ConexaoServico);
  private toast = inject(ToastServico);

  lista = signal<ConexaoModel[]>([]);
  limite = signal(1);
  podeAdicionar = signal(false);

  carregando = signal(true);
  erro = signal('');

  /** A conexão com o painel aberto (situação, QR e envio). Uma por vez: duas áreas de QR na
   *  mesma tela levariam a pessoa a escanear a errada. */
  abertaId = signal<number | null>(null);
  aberta = computed<ConexaoModel | null>(
    () => this.lista().find(c => c.id === this.abertaId()) ?? null);

  saude = signal<SaudeConexao | null>(null);
  qr = signal<QrCode | null>(null);

  /** Estado cru da Evolution para a conexão aberta. `offline` é distinto de `desconectado`:
   *  offline = a Evolution não respondeu (problema da infraestrutura, o usuário não tem o que
   *  fazer); desconectado = o número caiu e ele precisa reparear. */
  estado = signal<string>('');
  conectado = signal(false);

  gerandoQr = signal(false);
  numeroPareamento = signal('');
  modoPareamento = signal(false);

  // ---- nova conexão
  fNome = signal('');
  criando = signal(false);
  erroNovo = signal('');

  // ---- renomear em linha
  editandoId = signal<number | null>(null);
  eNome = signal('');

  /** A conexão cuja remoção está sendo confirmada. Apagar número é irreversível do lado da
   *  Evolution — a instância vai junto —, então não vai por `confirm()` do navegador. */
  removendo = signal<ConexaoModel | null>(null);

  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit() { this.carregar(); }

  ngOnDestroy() { this.pararPolling(); }

  // ---------------------------------------------------------------- lista
  carregar() {
    this.servico.listar().subscribe({
      next: r => {
        this.lista.set(r.itens);
        this.limite.set(r.limite);
        this.podeAdicionar.set(r.podeAdicionar);
        this.carregando.set(false);
        this.erro.set('');

        // Painel aberto sobre uma conexão que sumiu (apagada em outra aba): fecha em vez de
        // ficar mostrando dado velho.
        if (this.abertaId() !== null && !r.itens.some(c => c.id === this.abertaId())) {
          this.fechar();
        }
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar as conexões.');
        this.carregando.set(false);
      }
    });
  }

  abrir(c: ConexaoModel) {
    if (this.abertaId() === c.id) { this.fechar(); return; }

    this.pararPolling();
    this.abertaId.set(c.id);
    this.qr.set(null);
    this.saude.set(null);
    this.modoPareamento.set(false);
    this.estado.set('');
    this.conectado.set(c.status === 'conectado');

    this.servico.saude(c.id).subscribe({ next: s => this.saude.set(s), error: () => { } });
  }

  fechar() {
    this.pararPolling();
    this.abertaId.set(null);
    this.qr.set(null);
    this.saude.set(null);
    this.estado.set('');
  }

  // ---------------------------------------------------------------- pareamento
  gerarQr(id: number) {
    this.gerandoQr.set(true);
    this.erro.set('');
    this.modoPareamento.set(false);
    this.servico.conectar(id).subscribe({
      next: q => {
        this.qr.set(q);
        this.gerandoQr.set(false);
        if (q.conectado) this.toast.info('Este número já está conectado.');
        else this.comecarPolling(id);
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível gerar o QR code.');
        this.gerandoQr.set(false);
      }
    });
  }

  parear(id: number) {
    this.gerandoQr.set(true);
    this.erro.set('');
    this.servico.parear(id, this.numeroPareamento()).subscribe({
      next: q => {
        this.qr.set(q);
        this.gerandoQr.set(false);
        this.comecarPolling(id);
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

  desconectar(c: ConexaoModel) {
    if (!confirm(
      `Desconectar "${c.nome}"?\n\n` +
      'As mensagens desse número param de ser enviadas e recebidas. O histórico fica.')) return;

    this.servico.desconectar(c.id).subscribe({
      next: () => {
        this.pararPolling();
        this.conectado.set(false);
        this.qr.set(null);
        this.carregar();
      },
      error: e => this.erro.set(e.error?.erro ?? 'Não foi possível desconectar.')
    });
  }

  reconhecerTroca(c: ConexaoModel) {
    this.servico.reconhecerTroca(c.id).subscribe(() => this.carregar());
  }

  private comecarPolling(id: number) {
    this.pararPolling();
    // 3s: é assim que a tela descobre que o QR foi lido. O webhook connection.update também
    // chega, mas não dá para depender só dele com o QR na frente do usuário.
    this.timer = setInterval(() => this.verificarStatus(id), 3000);
    this.verificarStatus(id);
  }

  private pararPolling() {
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
  }

  private verificarStatus(id: number) {
    this.servico.status(id).subscribe({
      next: s => {
        const acabouDeConectar = s.conectado && !this.conectado();
        this.estado.set(s.estado);
        this.conectado.set(s.conectado);

        if (acabouDeConectar) {
          // Conectou: o QR não serve mais, e o número/perfil chegam pelo webhook.
          this.pararPolling();
          this.qr.set(null);
          this.toast.sucesso('WhatsApp conectado.');
          this.carregar();
        }
      },
      error: () => { }   // o polling não pode encher a tela de erro
    });
  }

  // ---------------------------------------------------------------- criar
  criar() {
    const nome = this.fNome().trim();
    if (nome.length < 2) { this.erroNovo.set('Dê um nome ao número.'); return; }

    this.criando.set(true);
    this.erroNovo.set('');
    this.servico.criar(nome).subscribe({
      next: r => {
        this.criando.set(false);
        this.fNome.set('');
        this.toast.sucesso(`"${nome}" criado. Agora conecte o celular.`);
        this.servico.listar().subscribe(l => {
          this.lista.set(l.itens);
          this.limite.set(l.limite);
          this.podeAdicionar.set(l.podeAdicionar);
          // Abre já no pareamento: criar um número sem conectar não serve para nada, e o
          // próximo passo é sempre o mesmo.
          const nova = l.itens.find(c => c.id === r.id);
          if (nova) this.abrir(nova);
        });
      },
      error: e => {
        this.criando.set(false);
        this.erroNovo.set(e.error?.erro ?? 'Não foi possível criar.');
      }
    });
  }

  // ---------------------------------------------------------------- renomear
  editar(c: ConexaoModel) {
    this.editandoId.set(c.id);
    this.eNome.set(c.nome);
  }

  cancelarEdicao() { this.editandoId.set(null); }

  salvarEdicao(c: ConexaoModel) {
    this.servico.renomear(c.id, this.eNome().trim()).subscribe({
      next: () => {
        this.editandoId.set(null);
        this.toast.sucesso('Nome atualizado.');
        this.carregar();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível salvar.')
    });
  }

  // ---------------------------------------------------------------- remover
  pedirRemocao(c: ConexaoModel) { this.removendo.set(c); }
  cancelarRemocao() { this.removendo.set(null); }

  confirmarRemocao() {
    const alvo = this.removendo();
    if (alvo === null) return;

    this.servico.remover(alvo.id).subscribe({
      next: () => {
        this.removendo.set(null);
        if (this.abertaId() === alvo.id) this.fechar();
        this.toast.sucesso(`"${alvo.nome}" apagado.`);
        this.carregar();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível apagar.')
    });
  }

  // ---------------------------------------------------------------- rótulos
  /** O estado do painel aberto: usa o ao vivo quando existe (durante o pareamento) e cai no
   *  persistido quando não. Sem o fallback, abrir uma conexão parada mostraria "—". */
  rotuloEstado(): string {
    const cru = this.estado();
    if (cru) return this.rotuloDoEstadoCru(cru);
    return this.rotuloDoStatus(this.aberta()?.status ?? '');
  }

  private rotuloDoEstadoCru(estado: string): string {
    switch (estado) {
      case 'open': return 'Conectado';
      case 'connecting': return 'Aguardando leitura do QR code';
      case 'close': return 'Desconectado';
      case 'nao_criada': return 'Ainda não conectado';
      case 'offline': return 'Serviço de WhatsApp indisponível';
      default: return '—';
    }
  }

  rotuloDoStatus(status: string): string {
    switch (status) {
      case 'conectado': return 'Conectado';
      case 'conectando': return 'Conectando';
      case 'desconectado': return 'Desconectado';
      case 'nao_criada': return 'Ainda não conectado';
      case 'offline': return 'Serviço indisponível';
      default: return '—';
    }
  }

  estaConectada(c: ConexaoModel): boolean { return c.status === 'conectado'; }
}
