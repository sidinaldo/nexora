import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Observable, forkJoin, map, of } from 'rxjs';
import {
  POR_PAGINA, Paginacao, alturaMinimaDaTabela, rolarParaTopoDaTabela, totalDePaginas
} from '../../nucleo/paginacao/paginacao';
import { ContatosServico, CorpoContato } from '../../nucleo/servicos/contatos.servico';
import { FunilServico } from '../../nucleo/servicos/funil.servico';
import { PipelinesServico } from '../../nucleo/servicos/pipelines.servico';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import {
  ColunaFunil, ContagemPorSituacao, ContatoResumo, FiltroContato, LinhaImportada, OrigemLead,
  ResumoImportacao, UsuarioEquipe
} from '../../nucleo/modelos';
import { iniciais } from '../../nucleo/iniciais';

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
  protected pipelines = inject(PipelinesServico);
  private equipe = inject(EquipeServico);
  private toast = inject(ToastServico);
  auth = inject(AuthServico);

  readonly filtros: OpcaoFiltro[] = [
    { chave: 'Abertos', rotulo: 'Em aberto' },
    { chave: 'Ganhos', rotulo: 'Ganhos' },
    { chave: 'Perdidos', rotulo: 'Perdidos' },
    { chave: 'Todos', rotulo: 'Todos' }
  ];

  /** O número ao lado de cada aba. `null` até a primeira resposta chegar — zero seria mentira
   *  enquanto a lista carrega, e "Ganhos 0" piscando é o tipo de dado falso que faz alguém
   *  desistir de clicar. */
  contagens = signal<ContagemPorSituacao | null>(null);

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

  /** ⚠️ ABRE EM "TODOS", E ISSO MUDOU. O padrão era "Em aberto", e quem fechava todos os
   *  negócios saía da tela sem nenhum aviso — "fechei os cards da Ysia em todos os funis e o
   *  contato sumiu da lista de contato".
   *
   *  A tela de Contatos é o diretório de PESSOAS; o estado do negócio é um recorte que se
   *  escolhe. Com as contagens ao lado de cada aba, escolher custa um clique e a carteira ativa
   *  continua a um clique de distância — o que não existia era o caminho de volta. */
  filtro = signal<FiltroContato>('Todos');
  busca = signal('');
  etapaId = signal<number | null>(null);
  responsavelId = signal<number | null>(null);
  /** A origem NÃO é filtro de servidor: a API não expõe esse parâmetro. Ver o comentário em
   *  `visiveis` para o porquê de ela ficar aqui assim mesmo. */
  origem = signal<OrigemLead | ''>('');

  /** As etapas de TODOS os funis, agrupadas — esta lista não é de um funil só.
   *
   *  ⚠️ O filtro corta `contatos` da empresa inteira, e desde que os funis viraram plurais um
   *  contato pode estar em qualquer um deles. Oferecer só as etapas de UM funil deixa os outros
   *  impossíveis de filtrar, e sem nada na tela explicando a ausência. */
  etapas = signal<{ funil: string; colunas: ColunaFunil[] }[]>([]);
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
   *  coisa numa base vazia manda a pessoa procurar um filtro que ela não aplicou.
   *
   *  ⚠️ A ABA CONTA COMO RECORTE, E ANTES NÃO CONTAVA. A linha era `filtro() !== 'Abertos'`,
   *  escrita como atalho para "o usuário escolheu algo" — e numa base em que todo mundo já
   *  comprou, a aba padrão ficava vazia e a tela dizia "Nenhum contato ainda. Cadastre um
   *  contato ou aguarde alguém mandar mensagem no WhatsApp". Mentira sobre uma base cheia.
   *
   *  Agora só "Todos" é a ausência de recorte — e "Todos" vazio é a única leitura em que a base
   *  está mesmo vazia. */
  temFiltro = computed(() =>
    this.filtro() !== 'Todos' || this.busca().trim() !== '' ||
    this.etapaId() !== null || this.responsavelId() !== null || this.origem() !== '');

  /** O avatar da linha. Uma copia so, em `nucleo/iniciais.ts`. */
  protected readonly iniciais = iniciais;

  // ==================================================================== importar (issue #8)
  /** ⚠️ DOIS PASSOS, e o estado da tela é a prévia existir ou não. `previa() === null` é "escolha
   *  o arquivo"; com prévia, é "confira e confirme". Um `passo: 1 | 2` seria um segundo jeito de
   *  dizer a mesma coisa, e os dois divergiriam no primeiro `catch`. */
  importAberto = signal(false);
  arquivo = signal<File | null>(null);
  previa = signal<ResumoImportacao | null>(null);
  importando = signal(false);
  erroImport = signal('');

  /** Nulo = só os contatos, sem card nenhum. É o padrão, e é a decisão 1 do bloco: importar 800
   *  clientes não pode encher o quadro de 800 cards. */
  funilDoImport = signal<number | null>(null);

  /** A caixinha "Avisar minhas integrações". Nasce do jeito que a PRÉVIA manda
   *  (`aviso.marcadoPorPadrao`) — quem decide o padrão é o servidor, e a tela só o desenha. */
  avisarIntegracoes = signal(false);

  abrirImport() {
    this.arquivo.set(null);
    this.previa.set(null);
    this.funilDoImport.set(null);
    this.avisarIntegracoes.set(false);
    this.erroImport.set('');
    this.importAberto.set(true);
    if (this.pipelines.lista().length === 0) this.pipelines.carregar().subscribe({ error: () => { } });
  }

  fecharImport() {
    if (this.importando()) return;
    this.importAberto.set(false);
  }

  /** Trocar de arquivo joga a prévia fora: ela descreve o arquivo ANTERIOR, e deixá-la na tela
   *  seria oferecer "confirmar" sobre números que não são mais daquele arquivo. */
  escolherArquivo(evento: Event) {
    const alvo = evento.target as HTMLInputElement;
    this.arquivo.set(alvo.files?.[0] ?? null);
    this.previa.set(null);
    this.erroImport.set('');
    if (this.arquivo()) this.conferir();
  }

  conferir() {
    const f = this.arquivo();
    if (!f || this.importando()) return;

    this.importando.set(true);
    this.servico.preverImportacao(f).subscribe({
      next: r => {
        this.previa.set(r);
        this.avisarIntegracoes.set(r.aviso.marcadoPorPadrao);
        this.importando.set(false);
      },
      error: e => {
        this.importando.set(false);
        this.previa.set(null);
        this.erroImport.set(e.error?.erro ?? 'Não foi possível ler o arquivo.');
      }
    });
  }

  confirmarImport() {
    const f = this.arquivo();
    if (!f || this.importando()) return;

    this.importando.set(true);
    const avisar = this.avisarIntegracoes();

    this.servico.importar(f, this.funilDoImport(), avisar).subscribe({
      next: r => {
        this.importando.set(false);
        this.importAberto.set(false);
        // Os avisos entram no FIM da fila de entregas (ver `EntregaWebhook.EmMassa`), então "saem
        // nos próximos minutos" é a promessa exata — e não "enviados", que ainda não foram.
        this.toast.sucesso(
          r.novas === 0
            ? 'Nenhum contato novo: todos já estavam na base.'
            : `${r.novas} contato${r.novas === 1 ? '' : 's'} importado${r.novas === 1 ? '' : 's'}.`
              + (avisar && r.aviso.disponivel
                  ? ' Os avisos para as suas integrações saem nos próximos minutos.' : ''));
        this.doZero();
      },
      error: e => {
        this.importando.set(false);
        this.erroImport.set(e.error?.erro ?? 'Não foi possível importar.');
      }
    });
  }

  rotuloSituacao(s: LinhaImportada['situacao']): string {
    return s === 'nova' ? 'entra' : s === 'repetida' ? 'já existe' : 'fora';
  }

  /** Quantos há na aba `f`, ou `null` enquanto a primeira resposta não chegou. */
  quantos(f: FiltroContato): number | null {
    const n = this.contagens();
    if (!n) return null;
    return f === 'Abertos' ? n.abertos
         : f === 'Ganhos' ? n.ganhos
         : f === 'Perdidos' ? n.perdidos
         : n.todos;
  }

  /** A altura mínima do CONTAINER, não das linhas. Só a partir da segunda página: numa lista de
   *  3 contatos no total, esticar a área para 20 linhas seria espaço morto sem motivo. */
  alturaMinima = computed(() =>
    this.totalPaginas() > 1 ? alturaMinimaDaTabela(this.tamanho) : 0);

  /** A coluna de VALOR só aparece se ALGUÉM na página tiver valor.
   *
   *  ===================== POR QUE ESCONDER =====================
   *  Valor é opcional, e a maioria das PMEs não preenche. Uma coluna inteira de travessões ocupa
   *  espaço horizontal — caro numa tabela de sete colunas que já rola no celular — e não informa
   *  nada: "nenhum destes negócios tem valor" é dito melhor pela ausência da coluna.
   *
   *  A decisão é POR PÁGINA, não pela base inteira: a página é o que está na tela, e consultar o
   *  total exigiria um dado que a API não devolve. O efeito colateral é a coluna aparecer e
   *  sumir ao paginar — aceitável, e melhor que uma coluna morta em toda página.
   *  ============================================================ */
  mostrarValor = computed(() => this.visiveis().some(c => c.valor != null && c.valor > 0));

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

  /** As etapas para o `<select>`, de TODOS os funis.
   *
   *  ⚠️ ISTO ERA `quadro(1)`, E ESTAVA ERRADO DESDE QUE A PIPELINE ENTROU NA ASSINATURA.
   *  A chamada nasceu quando o primeiro parâmetro era `porColuna` — e o comentário que estava
   *  aqui dizia exatamente isso: "porColuna=1 porque só interessam os NOMES das etapas". Quando
   *  `pipeline` entrou na frente, a chamada passou a pedir a pipeline de id 1 com 50 cards por
   *  coluna, e o comentário virou mentira sem ninguém tocar nele.
   *
   *  Funcionava por acidente na primeira empresa, cuja pipeline É a de id 1. Nas outras o filtro
   *  ficava vazio — sem erro e sem log.
   *
   *  `forkJoin` e não uma chamada só: não existe endpoint que devolva as etapas de todos os
   *  funis para qualquer papel (`/api/etapas` é só do dono). São no máximo 5 pipelines, cada uma
   *  com `porColuna: 1`, numa tela que não é de uso contínuo. */
  private carregarEtapas() {
    const funis = this.pipelines.lista();
    const fonte = funis.length > 0 ? of(funis) : this.pipelines.carregar();

    fonte.subscribe({
      next: ps => {
        if (ps.length === 0) { this.etapas.set([]); return; }

        forkJoin(ps.map(p => this.funil.quadro(p.id, 1))).subscribe({
          next: quadros => this.etapas.set(
            ps.map((p, i) => ({ funil: p.nome, colunas: quadros[i].colunas }))
              .filter(g => g.colunas.length > 0)),
          error: () => { }
        });
      },
      error: () => { }
    });
  }

  ngOnInit() {
    this.carregar();
    this.carregarEtapas();

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
        this.contagens.set(p.contagens);
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

  /** ⚠️ VOLTA PARA "TODOS", que é o padrão da tela desde que ela passou a abrir no diretório
   *  inteiro. Estava `'Abertos'` — ficou para trás na mudança do padrão —, e "Limpar" deixava um
   *  recorte ligado: `temFiltro()` continuava verdadeiro, e o estado vazio mandava o usuário voltar
   *  para "Todos", que era exatamente o que o botão deveria ter feito. */
  limparFiltros() {
    this.filtro.set('Todos');
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
  // ⚠️ O SELO DA LINHA NÃO É MAIS DECIDIDO AQUI. Havia um `situacao(c)` que refazia a regra das
  // abas no painel, e ela divergiu uma vez: `ganhoEm` perguntado antes de "tem negócio aberto?"
  // punha a Ysia como "venda fechada" DENTRO da aba "Em aberto". Agora o servidor manda
  // `c.situacao` pronto, montado com as mesmas expressões das abas (`RegrasNegociacao.Situacao`),
  // e a tela só o desenha.

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
