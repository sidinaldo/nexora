import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Observable, map } from 'rxjs';
import {
  POR_PAGINA, Paginacao, linhasFantasma, rolarParaTopoDaTabela, totalDePaginas
} from '../../nucleo/paginacao/paginacao';
import { ContatosServico, CorpoContato } from '../../nucleo/servicos/contatos.servico';
import { FunilServico } from '../../nucleo/servicos/funil.servico';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import {
  ColunaFunil, ContatoResumo, FiltroContato, OrigemLead, UsuarioEquipe
} from '../../nucleo/modelos';

interface OpcaoFiltro { chave: FiltroContato; rotulo: string; }

/** A LISTA DE CONTATOS.
 *
 *  Paginada por OFFSET, não por cursor — e é o contrário da caixa de entrada de propósito:
 *  cursor existe para lista que se REORDENA sozinha entre requisições (conversa nova sobe para
 *  o topo enquanto o vendedor rola). Contato não muda de nome sozinho, então offset é seguro
 *  aqui — e dá o total ("142 contatos"), que cursor não fornece. */
@Component({
  selector: 'app-contatos',
  imports: [FormsModule, DatePipe, RouterLink, Paginacao],
  templateUrl: './contatos.html',
  styleUrl: './contatos.css'
})
export class Contatos implements OnInit {
  private servico = inject(ContatosServico);
  private funil = inject(FunilServico);
  private equipe = inject(EquipeServico);
  private toast = inject(ToastServico);
  auth = inject(AuthServico);

  readonly filtros: OpcaoFiltro[] = [
    { chave: 'Abertos', rotulo: 'Em aberto' },
    { chave: 'Ganhos', rotulo: 'Ganhos' },
    { chave: 'Perdidos', rotulo: 'Perdidos' },
    { chave: 'Todos', rotulo: 'Todos' }
  ];

  readonly origens: OrigemLead[] = [
    'whatsapp', 'instagram', 'facebook', 'google', 'site', 'qrcode', 'indicacao', 'manual', 'outro'
  ];

  /** O mesmo tamanho de página de toda tabela do painel. Era 30 aqui e "tudo" em outras telas. */
  readonly tamanho = POR_PAGINA;

  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  itens = signal<ContatoResumo[]>([]);
  total = signal(0);
  pagina = signal(1);
  carregando = signal(true);
  erro = signal('');

  filtro = signal<FiltroContato>('Abertos');
  busca = signal('');
  etapaId = signal<number | null>(null);
  responsavelId = signal<number | null>(null);
  /** A origem NÃO é filtro de servidor: a API não expõe esse parâmetro. Ver o comentário em
   *  `visiveis` para o porquê de ela ficar aqui assim mesmo. */
  origem = signal<OrigemLead | ''>('');

  etapas = signal<ColunaFunil[]>([]);
  equipeLista = signal<UsuarioEquipe[]>([]);

  // Modal de cadastro / edição.
  editando = signal<ContatoResumo | null>(null);
  modalAberto = signal(false);
  salvando = signal(false);
  erroModal = signal('');

  fNome = signal('');
  fTelefone = signal('');
  fEmail = signal('');
  fOrigem = signal<OrigemLead>('manual');
  fResponsavel = signal<number | null>(null);
  fValor = signal<number | null>(null);
  fObservacoes = signal('');

  private buscaTimer?: ReturnType<typeof setTimeout>;

  totalPaginas = computed(() => totalDePaginas(this.total(), this.tamanho));

  /** Há algum recorte ligado? Muda o texto do estado vazio: "nenhum contato com esses filtros"
   *  orienta a limpar o filtro; "nenhum contato ainda" orienta a cadastrar. Dizer a primeira
   *  coisa numa base vazia manda a pessoa procurar um filtro que ela não aplicou. */
  temFiltro = computed(() =>
    this.filtro() !== 'Abertos' || this.busca().trim() !== '' ||
    this.etapaId() !== null || this.responsavelId() !== null || this.origem() !== '');

  /** Linhas vazias que seguram a altura da tabela na última página. */
  fantasmas = computed(() =>
    this.totalPaginas() > 1 ? linhasFantasma(this.visiveis().length, this.tamanho) : []);

  /** O recorte por ORIGEM acontece no cliente, sobre a página já carregada.
   *
   *  É uma limitação assumida, não um descuido: a API de listagem (bloco 7) filtra por etapa e
   *  responsável, mas não por origem. Filtrar no cliente sobre a página corrente é honesto para
   *  30 linhas e NÃO mente sobre o total — por isso a contagem exibida muda de rótulo quando
   *  este filtro está ligado. O filtro de servidor entra quando a API expuser o parâmetro. */
  visiveis = computed(() => {
    const o = this.origem();
    return o ? this.itens().filter(c => c.origem === o) : this.itens();
  });

  ngOnInit() {
    this.carregar();
    // porColuna=1 porque só interessam os NOMES das etapas para o filtro; carregar 50 cards por
    // coluna aqui seria pagar o quadro inteiro para preencher um <select>.
    this.funil.quadro(1).subscribe({ next: q => this.etapas.set(q.colunas), error: () => { } });

    // `GET /api/equipe` é [Authorize(Roles="dono")]: pedir como vendedor devolveria 403 e
    // sujaria o console sem necessidade. Sem a lista, o filtro por responsável não aparece.
    if (this.auth.ehDono()) {
      this.equipe.listar().subscribe({ next: us => this.equipeLista.set(us), error: () => { } });
    }
  }

  carregar() {
    this.carregando.set(true);
    this.servico.listar(
      this.filtro(), this.busca().trim() || undefined,
      this.etapaId(), this.responsavelId(), this.pagina(), this.tamanho
    ).subscribe({
      next: p => {
        this.itens.set(p.itens);
        this.total.set(p.total);
        this.carregando.set(false);
        this.erro.set('');
      },
      error: () => {
        this.erro.set('Não foi possível carregar os contatos.');
        this.carregando.set(false);
      }
    });
  }

  /** Trocar filtro volta para a página 1: manter a página com outro recorte mostraria "página 4
   *  de 2" e uma lista vazia sem explicação. */
  private doZero() { this.pagina.set(1); this.carregar(); }

  trocarFiltro(f: FiltroContato) { this.filtro.set(f); this.doZero(); }
  trocarEtapa(v: string) { this.etapaId.set(v ? Number(v) : null); this.doZero(); }
  trocarResponsavel(v: string) { this.responsavelId.set(v ? Number(v) : null); this.doZero(); }
  /** Também volta para a página 1, como os outros filtros: mesmo sendo recorte de cliente, ficar
   *  na página 8 depois de filtrar mostra tabela vazia com dado existindo nas páginas anteriores. */
  trocarOrigem(v: string) { this.origem.set(v as OrigemLead | ''); this.doZero(); }

  aoBuscar(valor: string) {
    this.busca.set(valor);
    if (this.buscaTimer) clearTimeout(this.buscaTimer);
    this.buscaTimer = setTimeout(() => this.doZero(), 350);
  }

  irPara(p: number) {
    if (p < 1 || p > this.totalPaginas()) return;
    this.pagina.set(p);
    this.carregar();
    // Topo da TABELA, não da janela: rolar a janela inteira faria a pessoa perder de vista o
    // filtro que acabou de aplicar.
    rolarParaTopoDaTabela(this.tabelaTopo?.nativeElement);
  }

  limparFiltros() {
    this.filtro.set('Abertos');
    this.busca.set('');
    this.etapaId.set(null);
    this.responsavelId.set(null);
    this.origem.set('');
    this.doZero();
  }

  // ---------------------------------------------------------------- cadastro
  abrirNovo() {
    this.editando.set(null);
    this.fNome.set(''); this.fTelefone.set(''); this.fEmail.set('');
    this.fOrigem.set('manual'); this.fResponsavel.set(null);
    this.fValor.set(null); this.fObservacoes.set('');
    this.erroModal.set('');
    this.modalAberto.set(true);
  }

  abrirEdicao(c: ContatoResumo, evento: Event) {
    evento.stopPropagation();
    this.editando.set(c);
    this.fNome.set(c.nome);
    this.fTelefone.set(c.telefone);
    this.fEmail.set(c.email ?? '');
    this.fOrigem.set(c.origem);
    this.fResponsavel.set(c.responsavelId);
    this.fValor.set(c.valor);
    this.fObservacoes.set('');
    this.erroModal.set('');
    this.modalAberto.set(true);
  }

  fecharModal() { this.modalAberto.set(false); }

  salvar() {
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
      this.erroModal.set('Nome e telefone são obrigatórios.');
      return;
    }

    this.salvando.set(true);
    this.erroModal.set('');

    const alvo = this.editando();
    // `criar` devolve { id } e `atualizar` devolve void — o union dos dois Observable não é
    // subscritível direto, então cada um é mapeado para void antes.
    const chamada: Observable<void> = alvo
      ? this.servico.atualizar(alvo.id, corpo)
      : this.servico.criar(corpo).pipe(map(() => void 0));

    chamada.subscribe({
      next: () => {
        this.salvando.set(false);
        this.modalAberto.set(false);
        this.toast.sucesso(alvo ? 'Contato atualizado.' : 'Contato cadastrado.');
        this.carregar();
      },
      error: (e: { error?: { erro?: string } }) => {
        this.salvando.set(false);
        this.erroModal.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  // ---------------------------------------------------------------- apoio
  situacao(c: ContatoResumo): 'ganho' | 'perdido' | 'aberto' {
    if (c.ganhoEm) return 'ganho';
    if (c.perdidoEm) return 'perdido';
    return 'aberto';
  }

  moeda(v: number | null): string {
    if (v === null || v === undefined) return '—';
    return v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
  }

  /** O telefone é guardado canônico (5584988887777); a tela mostra formatado. */
  telefoneVisivel(t: string): string {
    const d = (t ?? '').replace(/\D/g, '');
    if (d.length < 12 || !d.startsWith('55')) return t;
    const ddd = d.slice(2, 4);
    const resto = d.slice(4);
    const meio = resto.length === 9 ? resto.slice(0, 5) : resto.slice(0, 4);
    const fim = resto.length === 9 ? resto.slice(5) : resto.slice(4);
    return `(${ddd}) ${meio}-${fim}`;
  }
}
