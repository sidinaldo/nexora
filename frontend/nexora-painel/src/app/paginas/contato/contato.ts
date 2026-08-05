import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ContatosServico, CorpoContato } from '../../nucleo/servicos/contatos.servico';
import { FunilServico } from '../../nucleo/servicos/funil.servico';
import { MeuDiaServico } from '../../nucleo/servicos/meu-dia.servico';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import {
  ColunaFunil, ContatoDetalhe, LembreteDto, OrigemLead, UsuarioEquipe
} from '../../nucleo/modelos';
import { Thread } from '../../nucleo/thread/thread';
import {
  ModalFechamento, ResultadoFechamento, TipoFechamento
} from '../../nucleo/fechamento/modal-fechamento';

/** O DETALHE DO CONTATO: dados, conversa e lembretes numa tela só.
 *
 *  A CONVERSA é o mesmo `app-thread` da caixa de entrada — mesma paginação por cursor, mesma
 *  âncora de rolagem, mesmo compositor. Duplicar aquilo significaria consertar cada bug duas
 *  vezes, e descobrir o segundo meses depois na tela que ninguém testou.
 *
 *  As AÇÕES de venda e perda abrem o mesmo `app-modal-fechamento` do kanban: uma porta só. */
@Component({
  selector: 'app-contato',
  imports: [FormsModule, DatePipe, RouterLink, Thread, ModalFechamento],
  templateUrl: './contato.html',
  styleUrl: './contato.css'
})
export class Contato implements OnInit {
  private servico = inject(ContatosServico);
  private funil = inject(FunilServico);
  private lembretesApi = inject(MeuDiaServico);
  private equipe = inject(EquipeServico);
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

  /** A digitação tem que bater com o nome do contato para liberar a anonimização. */
  podeAnonimizar = computed(() =>
    this.confirmacaoNome().trim().toLowerCase() === (this.contato()?.nome ?? '').trim().toLowerCase());

  ngOnInit() {
    const id = Number(this.rota.snapshot.paramMap.get('id') ?? 0);
    this.id.set(id);
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
  }

  cancelarFechamento() { this.fechamento.set(null); this.erroFechamento.set(''); }

  confirmarFechamento(r: ResultadoFechamento) {
    this.salvandoFechamento.set(true);
    this.erroFechamento.set('');

    const chamada = r.tipo === 'ganho'
      ? this.servico.marcarGanho(this.id(), r.valor)
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
