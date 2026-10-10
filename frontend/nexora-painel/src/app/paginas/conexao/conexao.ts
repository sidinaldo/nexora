import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { API } from '../../nucleo/api-base';
import { ConexaoServico } from '../../nucleo/servicos/conexao.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { ModelosConexao } from './modelos-conexao/modelos-conexao';
import {
  CanalWhatsapp, Conexao as ConexaoModel, Conexoes, NovaConexao, QrCode, SaudeConexao, TesteConexao
} from '../../nucleo/modelos';
import { rotuloQualidadeMeta } from '../../nucleo/rotulos';
import { erroAo } from '../../nucleo/erros';

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
  imports: [FormsModule, DatePipe, ModelosConexao],
  templateUrl: './conexao.html',
  styleUrl: './conexao.css'
})
export class Conexao implements OnInit, OnDestroy {
  readonly rotuloQualidadeMeta = rotuloQualidadeMeta;
  private servico = inject(ConexaoServico);
  private toast = inject(ToastServico);

  lista = signal<ConexaoModel[]>([]);
  limite = signal(1);
  /** Quantos números a empresa tem, contado no servidor (AUD-XX). */
  emUso = signal(0);
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

  // ---- API oficial (INT-XX)
  /** O canal sugerido pela empresa. O formulário nasce nele, e a pessoa pode trocar. */
  canalPadrao = signal<CanalWhatsapp>('evolution');
  fCanal = signal<CanalWhatsapp>('evolution');
  /** Se a pessoa já escolheu o canal no formulário, o recarregar da lista não volta ao padrão. */
  private canalEscolhido = false;
  fPhoneNumberId = signal('');
  fWabaId = signal('');
  fToken = signal('');
  fAppSecret = signal('');
  /** ⚠️ O AVISO É CONFIRMADO, E NÃO SÓ MOSTRADO. Na API oficial o número sai do aplicativo do
   *  WhatsApp (celular e Web) e o histórico do aparelho não vem junto — quem descobre isso depois
   *  de criar perdeu o atendimento pelo celular sem ter escolhido. */
  fCiente = signal(false);

  /** O botão da conexão oficial só acende com tudo preenchido e o aviso confirmado. Quem confere
   *  as credenciais de verdade é o servidor, na Meta; aqui é só não oferecer um clique que já se
   *  sabe que vai dar erro. */
  oficialCompleto = computed(() =>
    this.fNome().trim().length >= 2
    && this.fPhoneNumberId().trim() !== ''
    && this.fWabaId().trim() !== ''
    && this.fToken().trim() !== ''
    && this.fAppSecret().trim() !== ''
    && this.fCiente());

  /** O resultado do "Testar conexão" da conexão aberta. */
  teste = signal<TesteConexao | null>(null);
  testando = signal(false);
  cToken = signal('');
  cAppSecret = signal('');

  /** Onde a Meta entrega as mensagens. É esta URL que o cliente cadastra no app dele. */
  readonly urlWebhook = `${API}/webhook/meta`;

  // ---- renomear em linha
  editandoId = signal<number | null>(null);
  eNome = signal('');

  /** A conexão cuja remoção está sendo confirmada. Apagar número é irreversível do lado da
   *  Evolution — a instância vai junto —, então não vai por `confirm()` do navegador. */
  removendo = signal<ConexaoModel | null>(null);

  private timer: ReturnType<typeof setInterval> | null = null;

  /** ===================== A LISTA DO BANCO, E DEPOIS A DE AGORA =====================
   *  ⚠️ O STATUS DA LISTA É O QUE O ÚLTIMO AVISO DA EVOLUTION GRAVOU, e o aviso se perde: um
   *  número caído passou seis dias aparecendo como "Conectado" nesta tela. Ao abrir, ela mostra a
   *  lista do banco, que é rápida, e pede UMA conferência ao servidor — ele pergunta à Evolution
   *  e corrige o banco. Uma vez, e não em polling: ver "NÃO HÁ POLLING DE STATUS" no spec.
   *  ================================================================================ */
  ngOnInit() { this.carregar(true); }

  ngOnDestroy() { this.pararPolling(); }

  // ---------------------------------------------------------------- lista
  carregar(conferir = false) {
    this.servico.listar().subscribe({
      next: r => {
        this.aplicar(r);
        if (conferir) this.conferir();
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar as conexões.');
        this.carregando.set(false);
      }
    });
  }

  private conferir() {
    this.servico.conferir().subscribe({
      next: r => {
        this.aplicar(r);
        // O painel aberto acompanha: o `conectado` dele foi lido da lista velha, no `abrir`. Com
        // o QR na tela quem manda é o polling do pareamento, e este não se mete.
        const a = this.aberta();
        if (a && !this.qr()) this.conectado.set(a.status === 'conectado');
      },
      // Sem erro na tela: a lista do banco já está nela, e a conferência periódica do servidor
      // corrige o status de qualquer jeito.
      error: () => { }
    });
  }

  private aplicar(r: Conexoes) {
    this.lista.set(r.itens);
    this.limite.set(r.limite);
    this.emUso.set(r.emUso);
    this.podeAdicionar.set(r.podeAdicionar);
    this.canalPadrao.set(r.canalPadrao ?? 'evolution');
    if (!this.canalEscolhido) this.fCanal.set(r.canalPadrao ?? 'evolution');
    this.carregando.set(false);
    this.erro.set('');

    // Painel aberto sobre uma conexão que sumiu (apagada em outra aba): fecha em vez de
    // ficar mostrando dado velho.
    if (this.abertaId() !== null && !r.itens.some(c => c.id === this.abertaId())) {
      this.fechar();
    }
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
    this.teste.set(null);
    this.cToken.set('');
    this.cAppSecret.set('');

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
        this.erro.set(e.error?.erro ?? 'Não foi possível gerar o QR Code.');
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
          this.toast.info('Não deu para gerar o código agora. Conecte pelo QR Code.');
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
      error: e => this.erro.set(erroAo(e, 'desconectar o número'))
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
  escolherCanal(canal: CanalWhatsapp) {
    this.canalEscolhido = true;
    this.fCanal.set(canal);
    this.erroNovo.set('');
  }

  criar() {
    const nome = this.fNome().trim();
    if (nome.length < 2) { this.erroNovo.set('Dê um nome ao número.'); return; }

    const oficial = this.fCanal() === 'cloud_api';
    const nova: NovaConexao = { nome, canal: this.fCanal() };

    if (oficial) {
      if (!this.fPhoneNumberId().trim() || !this.fWabaId().trim()
          || !this.fToken().trim() || !this.fAppSecret().trim()) {
        this.erroNovo.set('Preencha o Phone Number ID, o WABA ID, o token e o app secret.');
        return;
      }
      if (!this.fCiente()) {
        this.erroNovo.set('Confirme que entendeu o aviso sobre o aplicativo do WhatsApp.');
        return;
      }
      nova.phoneNumberId = this.fPhoneNumberId().trim();
      nova.wabaId = this.fWabaId().trim();
      nova.accessToken = this.fToken().trim();
      nova.appSecret = this.fAppSecret().trim();
    }

    this.criando.set(true);
    this.erroNovo.set('');
    this.servico.criar(nova).subscribe({
      next: r => {
        this.criando.set(false);
        this.fNome.set('');
        this.limparOficial();
        this.toast.sucesso(oficial
          ? `"${nome}" conectado pela API oficial. Falta cadastrar o webhook no app da Meta.`
          : `"${nome}" criado. Agora conecte o celular.`);
        this.servico.listar().subscribe(l => {
          this.lista.set(l.itens);
          this.limite.set(l.limite);
          this.emUso.set(l.emUso);
          this.podeAdicionar.set(l.podeAdicionar);
          // Abre já no pareamento: criar um número sem conectar não serve para nada, e o
          // próximo passo é sempre o mesmo.
          const nova = l.itens.find(c => c.id === r.id);
          if (nova) this.abrir(nova);
        });
      },
      error: e => {
        this.criando.set(false);
        this.erroNovo.set(erroAo(e, 'criar o número'));
      }
    });
  }

  /** Os segredos saem do formulário assim que viram conexão: ficar na memória da tela não serve
   *  a ninguém. */
  private limparOficial() {
    this.fPhoneNumberId.set('');
    this.fWabaId.set('');
    this.fToken.set('');
    this.fAppSecret.set('');
    this.fCiente.set(false);
  }

  definirPadrao(canal: CanalWhatsapp) {
    this.servico.definirCanalPadrao(canal).subscribe({
      next: () => {
        this.canalPadrao.set(canal);
        this.toast.sucesso(canal === 'cloud_api'
          ? 'Novos números vão sugerir a API oficial.'
          : 'Novos números vão sugerir a conexão por QR Code.');
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível salvar o padrão.')
    });
  }

  // ---------------------------------------------------------------- API oficial (INT-XX)
  testar(id: number) {
    this.testando.set(true);
    this.servico.testar(id).subscribe({
      next: t => { this.teste.set(t); this.testando.set(false); },
      error: e => {
        this.testando.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível testar a conexão.');
      }
    });
  }

  salvarCredenciais(c: ConexaoModel) {
    const token = this.cToken().trim() || null;
    const segredo = this.cAppSecret().trim() || null;
    if (token === null && segredo === null) return;

    this.servico.atualizarCredenciais(c.id, token, segredo).subscribe({
      next: () => {
        this.cToken.set('');
        this.cAppSecret.set('');
        this.toast.sucesso('Credenciais atualizadas.');
        this.carregar();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível salvar as credenciais.')
    });
  }

  copiar(texto: string | null) {
    if (!texto) return;
    navigator.clipboard?.writeText(texto).then(
      () => this.toast.sucesso('Copiado.'),
      () => this.toast.erro('Não foi possível copiar. Selecione e copie à mão.'));
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
      error: e => this.toast.erro(erroAo(e, 'salvar o número'))
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
      error: e => this.toast.erro(erroAo(e, 'apagar o número'))
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
      case 'connecting': return 'Aguardando leitura do QR Code';
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
