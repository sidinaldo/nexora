import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ContatosServico, CorpoContato } from '../../nucleo/servicos/contatos.servico';
import { FunilServico } from '../../nucleo/servicos/funil.servico';
import { MeuDiaServico } from '../../nucleo/servicos/meu-dia.servico';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { VendasServico } from '../../nucleo/servicos/vendas.servico';
import { TrilhaServico } from '../../nucleo/servicos/trilha.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import {
  ColunaFunil, ContatoDetalhe, EventoTrilha, LembreteDto, OrigemLead, UsuarioEquipe, VendaDto
} from '../../nucleo/modelos';
import { Thread } from '../../nucleo/thread/thread';
import {
  Paginacao, fatiar, rolarParaTopoDaTabela, totalDePaginas
} from '../../nucleo/paginacao/paginacao';
import {
  ModalFechamento, OpcaoCanal, ResultadoFechamento, TipoFechamento
} from '../../nucleo/fechamento/modal-fechamento';

/** Nome de campo -> palavra que o vendedor usa. Sem isto a linha do tempo diria
 *  "editou responsavelId", que é linguagem de banco na tela de quem nunca vai abrir o banco. */
const ROTULOS: Record<string, string> = {
  nome: 'o nome', telefone: 'o telefone', email: 'o e-mail',
  valor: 'o valor', observacoes: 'as observações', origem: 'a origem',
  origemDetalhe: 'o detalhe da origem', responsavelId: 'o responsável',
  etapa: 'a etapa', motivoPerda: 'o motivo da perda'
};

/** O DETALHE DO CONTATO: dados, conversa e lembretes numa tela só.
 *
 *  A CONVERSA é o mesmo `app-thread` da caixa de entrada — mesma paginação por cursor, mesma
 *  âncora de rolagem, mesmo compositor. Duplicar aquilo significaria consertar cada bug duas
 *  vezes, e descobrir o segundo meses depois na tela que ninguém testou.
 *
 *  As AÇÕES de venda e perda abrem o mesmo `app-modal-fechamento` do kanban: uma porta só. */
@Component({
  selector: 'app-contato',
  imports: [FormsModule, DatePipe, RouterLink, Thread, ModalFechamento, Paginacao],
  templateUrl: './contato.html',
  styleUrl: './contato.css'
})
export class Contato implements OnInit {
  private servico = inject(ContatosServico);
  private funil = inject(FunilServico);
  private lembretesApi = inject(MeuDiaServico);
  private equipe = inject(EquipeServico);
  private vendasApi = inject(VendasServico);
  private trilhaApi = inject(TrilhaServico);
  private toast = inject(ToastServico);
  private rota = inject(ActivatedRoute);
  private router = inject(Router);
  auth = inject(AuthServico);

  readonly origens: OrigemLead[] = [
    'whatsapp', 'instagram', 'facebook', 'google', 'site', 'qrcode', 'indicacao', 'manual', 'outro'
  ];

  id = signal(0);
  dados = signal<ContatoDetalhe | null>(null);
  carregando = signal(true);
  erro = signal('');

  etapas = signal<ColunaFunil[]>([]);
  equipeLista = signal<UsuarioEquipe[]>([]);

  // edição
  editando = signal(false);
  salvando = signal(false);
  erroEdicao = signal('');
  fNome = signal('');
  fTelefone = signal('');
  fEmail = signal('');
  fOrigem = signal<OrigemLead>('manual');
  fResponsavel = signal<number | null>(null);
  fValor = signal<number | null>(null);
  fObservacoes = signal('');

  // fechamento (venda / perda) — o MESMO modal do kanban
  fechamento = signal<TipoFechamento | null>(null);
  salvandoFechamento = signal(false);
  erroFechamento = signal('');
  /** NEG-3 · as campanhas oferecidas no modal, e a que o sistema detectou nesta conversa. */
  canaisFechamento = signal<OpcaoCanal[]>([]);
  canalDetectado = signal<number | null>(null);

  // anonimização
  modalAnonimizar = signal(false);
  confirmacaoNome = signal('');
  anonimizando = signal(false);

  // lembrete novo
  modalLembrete = signal(false);
  lTitulo = signal('');
  lData = signal('');
  lHora = signal('');
  lObservacao = signal('');
  salvandoLembrete = signal(false);
  erroLembrete = signal('');

  contato = computed(() => this.dados()?.contato ?? null);
  anonimizado = computed(() => !!this.dados()?.anonimizadoEm);

  situacao = computed<'ganho' | 'perdido' | 'aberto'>(() => {
    const c = this.contato();
    if (c?.ganhoEm) return 'ganho';
    if (c?.perdidoEm) return 'perdido';
    return 'aberto';
  });

  lembretesPendentes = computed(() =>
    this.dados()?.lembretes.filter(l => l.status === 'pendente') ?? []);
  lembretesFeitos = computed(() =>
    this.dados()?.lembretes.filter(l => l.status !== 'pendente') ?? []);

  /** Só os CONCLUÍDOS paginam — ver o comentário no template. Os pendentes são a lista
   *  acionável e aparecem inteiros. */
  paginaFeitos = signal(1);
  @ViewChild('listaFeitos') private listaFeitos?: ElementRef<HTMLElement>;

  totalPaginasFeitos = computed(() => totalDePaginas(this.lembretesFeitos().length));
  feitosVisiveis = computed(() => fatiar(this.lembretesFeitos(), this.paginaFeitos()));

  irParaFeitos(p: number) {
    this.paginaFeitos.set(p);
    rolarParaTopoDaTabela(this.listaFeitos?.nativeElement);
  }

  /** A digitação tem que bater com o nome do contato para liberar a anonimização. */
  podeAnonimizar = computed(() =>
    this.confirmacaoNome().trim().toLowerCase() === (this.contato()?.nome ?? '').trim().toLowerCase());

  /** `?lembrete=N` — o Meu Dia manda o vendedor para o lembrete específico.
   *
   *  Sem isto, clicar num follow-up abria uma tela com cinco lembretes e ele precisava
   *  reencontrar o que estava fazendo. O destaque é visual e temporário: some ao concluir ou
   *  cancelar, porque a partir daí a linha não é mais a tarefa. */
  lembreteEmFoco = signal<number | null>(null);

  ngOnInit() {
    const id = Number(this.rota.snapshot.paramMap.get('id') ?? 0);
    this.id.set(id);

    const foco = Number(this.rota.snapshot.queryParamMap.get('lembrete') ?? 0);
    if (foco) this.lembreteEmFoco.set(foco);

    this.carregar();

    this.funil.quadro(1).subscribe({ next: q => this.etapas.set(q.colunas), error: () => { } });
    if (this.auth.ehDono()) {
      this.equipe.listar().subscribe({ next: us => this.equipeLista.set(us), error: () => { } });
    }
  }

  carregar() {
    this.carregando.set(true);
    this.servico.detalhe(this.id()).subscribe({
      next: d => { this.dados.set(d); this.carregando.set(false); this.erro.set(''); },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Contato não encontrado.');
        this.carregando.set(false);
      }
    });

    // Chamada SEPARADA, e o erro dela não derruba a tela: o histórico de vendas é informação
    // complementar, e um contato sem venda nenhuma é o caso comum.
    this.vendasApi.doContato(this.id()).subscribe({
      next: v => this.vendas.set(v),
      error: () => this.vendas.set([])
    });

    // Só quem pode ver: pedir e receber 403 encheria o console de erro a cada abertura de
    // contato. A regra que VALE é a do servidor; esta só evita o pedido inútil.
    if (this.auth.ehDono() || this.auth.ehGestor()) {
      this.trilhaApi.doContato(this.id()).subscribe({
        next: t => this.trilha.set(t),
        error: () => this.trilha.set([])
      });
    }
  }

  // ---------------------------------------------------------------- trilha (AUD-1)
  trilha = signal<EventoTrilha[]>([]);

  /** ===================== A TRADUÇÃO MORA AQUI, NÃO NO SERVIDOR =====================
   *  "moveu de Negociação para Proposta", nunca "etapa_id: 4 → 3". Nome de coluna na tela é
   *  linguagem de banco vazando para quem nunca vai abrir o banco.
   *
   *  No CLIENTE porque é texto de interface: muda com a redação do produto, e traduzir no
   *  servidor obrigaria a um deploy de backend para corrigir uma frase. */
  frase(e: EventoTrilha): string {
    const a = this.alteracoesDe(e);
    const nomeDe = (c: string) => ROTULOS[c] ?? c;
    const valor = (c: string, lado: 'antes' | 'depois') => a[c]?.[lado];

    switch (e.acao) {
      case 'Criou':
        return e.entidade === 'Venda' ? 'registrou uma venda' : 'cadastrou o contato';
      case 'Moveu':
        return `moveu de ${valor('etapa', 'antes') ?? '—'} para ${valor('etapa', 'depois') ?? '—'}`;
      case 'Ganhou': return 'marcou venda fechada';
      case 'Perdeu': return 'marcou como perdido';
      case 'Reabriu': return 'reabriu a negociação';
      case 'Cancelou': return 'cancelou a venda';
      case 'Anonimizou': return 'anonimizou o contato';
      case 'Atribuiu': return 'mudou o responsável pelo atendimento';
      case 'Editou': {
        const campos = Object.keys(a).map(nomeDe);
        return campos.length ? `editou ${campos.join(', ')}` : 'editou o contato';
      }
      default: return e.acao.toLowerCase();
    }
  }

  /** Quem agiu. `Sistema` NÃO vira um nome inventado: a ação foi de um job, e dizer o contrário
   *  seria autoria falsa — o problema que a trilha existe para evitar. */
  quem(e: EventoTrilha): string {
    return e.ator === 'Sistema' ? 'Sistema' : (e.usuarioNome ?? 'Usuário removido');
  }

  private alteracoesDe(e: EventoTrilha): Record<string, { antes?: string; depois?: string }> {
    // JSON malformado não pode derrubar a tela do contato inteira por causa de um evento.
    try { return JSON.parse(e.alteracoes ?? '{}') ?? {}; } catch { return {}; }
  }

  // ---------------------------------------------------------------- vendas (NEG-1)
  vendas = signal<VendaDto[]>([]);
  cancelando = signal<number | null>(null);
  concluindo = signal<number | null>(null);

  /** O resumo de "já comprou antes", ou `null` quando não comprou.
   *
   *  Canceladas ficam de FORA: venda desfeita não é histórico de compra, e chamar de recorrente
   *  quem teve uma venda marcada por engano seria pior que não dizer nada.
   *
   *  CONCLUÍDAS ENTRAM (NEG-2), e é o ponto: um pedido entregue é a prova mais forte de que a
   *  pessoa é cliente. Filtrá-lo junto com o cancelado faria o cliente mais antigo aparecer como
   *  lead novo — exatamente a confusão que este bloco veio desfazer. */
  resumoVendas = computed(() => {
    const validas = this.vendas().filter(v => v.status !== 'cancelada');
    if (validas.length === 0) return null;

    return {
      quantidade: validas.length,
      total: validas.reduce((s, v) => s + v.valor, 0),
      ultimaEm: validas.map(v => v.fechadaEm).sort().at(-1) ?? null
    };
  });

  cancelarVenda(v: VendaDto) {
    // `confirm` do navegador: cancelar tira faturamento da contagem, e é ação de gestor sobre
    // número fechado. Vale o atrito de um clique a mais.
    if (!confirm(`Cancelar a venda de ${this.moeda(v.valor)}? A linha continua no histórico, riscada.`)) return;

    this.cancelando.set(v.id);
    this.vendasApi.cancelar(v.id).subscribe({
      next: () => {
        this.cancelando.set(null);
        this.toast.sucesso('Venda cancelada.');
        this.carregar();   // o carimbo do contato pode ter mudado junto
      },
      error: e => {
        this.cancelando.set(null);
        this.toast.erro(e.error?.erro ?? 'Não foi possível cancelar a venda.');
      }
    });
  }

  /** "Esse pedido acabou" (NEG-2).
   *
   *  SEM `confirm`, ao contrário de cancelar: concluir não tira dinheiro de lugar nenhum e é
   *  reversível na prática (o gestor ainda pode cancelar). Pedir confirmação para a ação que a
   *  empresa precisa que aconteça trinta vezes por semana é o jeito mais rápido de ninguém
   *  fazê-la — e aí a coluna volta a acumular. */
  concluirVenda(v: VendaDto) {
    this.concluindo.set(v.id);
    this.vendasApi.concluir([v.id]).subscribe({
      next: r => {
        this.concluindo.set(null);
        if (r.concluidas === 0) {
          // Zero tem explicação: alguém concluiu ou cancelou entre a leitura e o clique.
          this.toast.erro('Este pedido já havia sido fechado. A lista foi atualizada.');
        } else {
          this.toast.sucesso('Pedido concluído. O valor continua no faturamento.');
        }
        this.carregar();
      },
      error: e => {
        this.concluindo.set(null);
        this.toast.erro(e.error?.erro ?? 'Não foi possível concluir o pedido.');
      }
    });
  }

  // ---------------------------------------------------------------- edição
  abrirEdicao() {
    const c = this.contato();
    const d = this.dados();
    if (!c || !d) return;
    this.fNome.set(c.nome);
    this.fTelefone.set(c.telefone);
    this.fEmail.set(c.email ?? '');
    this.fOrigem.set(c.origem);
    this.fResponsavel.set(c.responsavelId);
    this.fValor.set(c.valor);
    this.fObservacoes.set(d.observacoes ?? '');
    this.erroEdicao.set('');
    this.editando.set(true);
  }

  cancelarEdicao() { this.editando.set(false); }

  salvarEdicao() {
    const corpo: CorpoContato = {
      nome: this.fNome().trim(),
      telefone: this.fTelefone().trim(),
      email: this.fEmail().trim() || null,
      origem: this.fOrigem(),
      responsavelId: this.fResponsavel(),
      valor: this.fValor(),
      observacoes: this.fObservacoes().trim() || null
    };

    if (!corpo.nome || !corpo.telefone) {
      this.erroEdicao.set('Nome e telefone são obrigatórios.');
      return;
    }

    this.salvando.set(true);
    this.erroEdicao.set('');
    this.servico.atualizar(this.id(), corpo).subscribe({
      next: () => {
        this.salvando.set(false);
        this.editando.set(false);
        this.toast.sucesso('Contato atualizado.');
        this.carregar();
      },
      error: e => {
        this.salvando.set(false);
        this.erroEdicao.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  // ---------------------------------------------------------------- mover de etapa
  /** O `<select>` de etapa é o equivalente do arrastar, e obedece à MESMA regra: escolher a
   *  etapa de venda abre o modal em vez de chamar `mover`, porque a API recusa. */
  mudarEtapa(valor: string) {
    const destino = Number(valor);
    const c = this.contato();
    if (!c || !destino || destino === c.etapaId) return;

    if (this.etapas().find(e => e.etapaId === destino)?.eGanho) {
      this.abrirFechamento('ganho');
      return;
    }

    // aposContatoId null = topo da coluna de destino.
    this.funil.mover(this.id(), destino, null).subscribe({
      next: () => { this.toast.sucesso('Contato movido.'); this.carregar(); },
      error: e => {
        this.toast.erro(e.error?.erro ?? 'Não foi possível mover o contato.');
        this.carregar();   // devolve o select ao valor real
      }
    });
  }

  // ---------------------------------------------------------------- fechamento
  abrirFechamento(tipo: TipoFechamento) {
    this.erroFechamento.set('');
    this.fechamento.set(tipo);
    if (tipo === 'ganho') this.carregarCanais();
  }

  cancelarFechamento() {
    this.fechamento.set(null);
    this.erroFechamento.set('');
    // Zera os canais junto: reabrir o modal tem que refazer a leitura, senão uma campanha criada
    // no meio da sessão só apareceria depois de recarregar a página.
    this.canaisFechamento.set([]);
    this.canalDetectado.set(null);
  }

  /** ⚠️ FALHA EM SILÊNCIO, de propósito. O canal é opcional; derrubar o fechamento inteiro porque
   *  a lista de campanhas não veio trocaria um campo a menos por uma venda não registrada. */
  private carregarCanais() {
    this.servico.canaisDoFechamento(this.id()).subscribe({
      next: r => { this.canaisFechamento.set(r.canais); this.canalDetectado.set(r.detectadoId); },
      error: () => { this.canaisFechamento.set([]); this.canalDetectado.set(null); }
    });
  }

  confirmarFechamento(r: ResultadoFechamento) {
    this.salvandoFechamento.set(true);
    this.erroFechamento.set('');

    const chamada = r.tipo === 'ganho'
      ? this.servico.marcarGanho(this.id(), r.valor, r.canalId)
      : this.servico.marcarPerdido(this.id(), r.motivo);

    chamada.subscribe({
      next: () => {
        this.salvandoFechamento.set(false);
        this.fechamento.set(null);
        this.toast.sucesso(r.tipo === 'ganho' ? 'Venda registrada.' : 'Contato marcado como perdido.');
        this.carregar();
      },
      error: e => {
        this.salvandoFechamento.set(false);
        this.erroFechamento.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  reabrir() {
    this.servico.reabrir(this.id()).subscribe({
      next: () => { this.toast.sucesso('Negociação reaberta.'); this.carregar(); },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível reabrir.')
    });
  }

  // ---------------------------------------------------------------- LGPD
  abrirAnonimizar() {
    this.confirmacaoNome.set('');
    this.modalAnonimizar.set(true);
  }

  anonimizar() {
    if (!this.podeAnonimizar()) return;
    this.anonimizando.set(true);
    this.servico.anonimizar(this.id()).subscribe({
      next: () => {
        this.anonimizando.set(false);
        this.modalAnonimizar.set(false);
        this.toast.sucesso('Dados pessoais apagados. O histórico foi preservado.');
        this.carregar();
      },
      error: e => {
        this.anonimizando.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível anonimizar.');
      }
    });
  }

  // ---------------------------------------------------------------- lembretes
  abrirLembrete() {
    const hoje = new Date();
    this.lTitulo.set('');
    this.lData.set(
      `${hoje.getFullYear()}-${String(hoje.getMonth() + 1).padStart(2, '0')}-${String(hoje.getDate()).padStart(2, '0')}`);
    this.lHora.set('');
    this.lObservacao.set('');
    this.erroLembrete.set('');
    this.modalLembrete.set(true);
  }

  salvarLembrete() {
    const titulo = this.lTitulo().trim();
    if (!titulo) { this.erroLembrete.set('Dê um título ao lembrete.'); return; }
    if (!this.lData()) { this.erroLembrete.set('Escolha a data.'); return; }

    this.salvandoLembrete.set(true);
    this.erroLembrete.set('');
    this.lembretesApi.criar({
      contatoId: this.id(),
      dataAlvo: this.lData(),
      horaAlvo: this.lHora() || null,
      titulo,
      observacao: this.lObservacao().trim() || null
    }).subscribe({
      next: () => {
        this.salvandoLembrete.set(false);
        this.modalLembrete.set(false);
        this.toast.sucesso('Lembrete criado.');
        this.carregar();
      },
      error: e => {
        this.salvandoLembrete.set(false);
        this.erroLembrete.set(e.error?.erro ?? 'Não foi possível criar o lembrete.');
      }
    });
  }

  concluirLembrete(l: LembreteDto) {
    this.lembretesApi.concluir(l.id).subscribe({
      next: () => { this.toast.sucesso('Lembrete concluído.'); this.carregar(); },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível concluir.')
    });
  }

  cancelarLembrete(l: LembreteDto) {
    this.lembretesApi.cancelar(l.id).subscribe({
      next: () => { this.toast.info('Lembrete cancelado.'); this.carregar(); },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível cancelar.')
    });
  }

  // ---------------------------------------------------------------- apoio
  voltar() { this.router.navigate(['/contatos']); }

  moeda(v: number | null): string {
    if (v === null || v === undefined) return '—';
    return v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
  }

  telefoneVisivel(t: string): string {
    const d = (t ?? '').replace(/\D/g, '');
    if (d.length < 12 || !d.startsWith('55')) return t;
    const ddd = d.slice(2, 4);
    const resto = d.slice(4);
    const meio = resto.length === 9 ? resto.slice(0, 5) : resto.slice(0, 4);
    const fim = resto.length === 9 ? resto.slice(5) : resto.slice(4);
    return `(${ddd}) ${meio}-${fim}`;
  }

  hora(l: LembreteDto): string { return l.horaAlvo ? l.horaAlvo.substring(0, 5) : ''; }
}
