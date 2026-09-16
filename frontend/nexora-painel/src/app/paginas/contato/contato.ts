import { catchError, forkJoin, of } from 'rxjs';
import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ContatosServico, CorpoContato } from '../../nucleo/servicos/contatos.servico';
import { FunilServico } from '../../nucleo/servicos/funil.servico';
import { PipelinesServico } from '../../nucleo/servicos/pipelines.servico';
import { EtapasServico } from '../../nucleo/servicos/etapas.servico';
import { MeuDiaServico } from '../../nucleo/servicos/meu-dia.servico';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { VendasServico } from '../../nucleo/servicos/vendas.servico';
import { TrilhaServico } from '../../nucleo/servicos/trilha.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { EtiquetasServico } from '../../nucleo/servicos/etiquetas.servico';
import { SeletorEtiquetas } from '../../nucleo/etiquetas/seletor-etiquetas';
import { textoSobre } from '../../nucleo/cor';
import {
  ColunaFunil, ContatoDetalhe, EtapaConfigDto, EtiquetaDto, EventoTrilha, LembreteDto, NegocioDoContato, OrigemLead, UsuarioEquipe, VendaDto
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
  imports: [FormsModule, DatePipe, RouterLink, Thread, ModalFechamento, Paginacao, SeletorEtiquetas],
  templateUrl: './contato.html',
  styleUrl: './contato.css'
})
export class Contato implements OnInit {
  private servico = inject(ContatosServico);
  private funil = inject(FunilServico);
  /** Carregada pelo shell no boot — o seletor de funil não custa requisição. */
  readonly pipelines = inject(PipelinesServico);
  private etapasApi = inject(EtapasServico);
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

  /** ===================== AS ETAPAS DE CADA FUNIL DA LISTA =====================
   *  ⚠️ ERA UM ARRAY SÓ, para UM seletor. Com a pessoa em Vendas e em Pós-venda, cada linha
   *  precisa das etapas DO SEU funil — um array só serviria a uma delas e mentiria na outra.
   *
   *  Carregadas por `GET /api/etapas?pipeline=`, que é a rota LEVE. A versão anterior usava
   *  `quadro(pipelineId, 1)` — o quadro inteiro com um card por coluna — só para ler os nomes
   *  das colunas. Com N funis isso seriam N consultas de quadro. */
  etapasPorFunil = signal<Record<number, EtapaConfigDto[]>>({});

  etapasDe(pipelineId: number): EtapaConfigDto[] {
    return this.etapasPorFunil()[pipelineId] ?? [];
  }

  negocios = computed<NegocioDoContato[]>(() => this.dados()?.negocios ?? []);

  /** ===================== A TERCEIRA CAMADA DA MESMA REGRA =====================
   *  Os funis onde esta pessoa ainda NÃO aparece no quadro — os únicos em que "Abrir negociação"
   *  pode dar certo.
   *
   *  ⚠️ NÃO É VALIDAÇÃO, é a ausência da opção impossível. A regra é garantida pelo banco
   *  (`uq_negociacoes_card_por_funil`) e explicada pelo serviço (que recusa dizendo o nome do
   *  funil); aqui ela só evita oferecer um clique que sempre erra — o que este projeto já trata
   *  como defeito por escrito.
   *
   *  ⚠️ `aberta` E `ganha`, e o filtro por `aberta` sozinho foi um DEFEITO REAL: a tela oferecia
   *  o funil onde a pessoa tinha uma venda esperando conclusão, e o clique voltava 409. É o mesmo
   *  par de estados do índice e do `FunisOcupados` da caixa — os três dizem a mesma coisa porque
   *  respondem à mesma pergunta: "esta pessoa já tem um card aqui?". */
  funisDisponiveis = computed(() => {
    const ocupados = new Set(
      this.negocios()
        .filter(n => n.status === 'aberta' || n.status === 'ganha')
        .map(n => n.pipelineId));
    return this.pipelines.lista().filter(p => !ocupados.has(p.id));
  });
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

  // ---------------------------------------------------------------- etiquetas
  private etiquetasApi = inject(EtiquetasServico);

  textoSobre = textoSobre;

  /** As do contato e o vocabulário inteiro. Separados de propósito: o vocabulário só é buscado
   *  quando o seletor abre — a tela de contato não precisa dele para desenhar os chips. */
  etiquetas = signal<EtiquetaDto[]>([]);
  vocabulario = signal<EtiquetaDto[]>([]);
  selecionandoEtiquetas = signal(false);
  salvandoEtiquetas = signal(false);
  erroEtiquetas = signal('');

  private carregarEtiquetas() {
    // Falha em silêncio: a tela inteira não pode deixar de abrir porque os chips não vieram.
    this.etiquetasApi.doContato(this.id()).subscribe({
      next: l => this.etiquetas.set(l),
      error: () => { }
    });
  }

  abrirEtiquetas() {
    this.erroEtiquetas.set('');
    this.selecionandoEtiquetas.set(true);
    this.etiquetasApi.listar().subscribe({
      next: l => this.vocabulario.set(l),
      error: () => { }
    });
  }

  /** ⚠️ NULO = as etiquetas da PESSOA; preenchido = as DAQUELE negócio. São duas tabelas porque
   *  respondem a perguntas diferentes: "Revendedor" vale em todos os negócios dela, "Urgente"
   *  vale num card só. */
  etiquetandoNegocio = signal<NegocioDoContato | null>(null);

  abrirEtiquetasDoNegocio(negocio: NegocioDoContato) {
    this.erroEtiquetas.set('');
    this.etiquetandoNegocio.set(negocio);
    this.selecionandoEtiquetas.set(true);
    this.etiquetasApi.listar().subscribe({
      next: l => this.vocabulario.set(l),
      error: () => { }
    });
  }

  cancelarEtiquetas() {
    this.etiquetandoNegocio.set(null);
    this.selecionandoEtiquetas.set(false);
    this.erroEtiquetas.set('');
    this.vocabulario.set([]);
  }

  confirmarEtiquetas(ids: number[]) {
    if (this.salvandoEtiquetas()) return;
    this.salvandoEtiquetas.set(true);
    this.erroEtiquetas.set('');

    const negocio = this.etiquetandoNegocio();

    const chamada = negocio
      ? this.etiquetasApi.aplicarNaNegociacao(negocio.id, ids)
      : this.etiquetasApi.aplicar(this.id(), ids);

    chamada.subscribe({
      next: () => {
        this.salvandoEtiquetas.set(false);
        this.selecionandoEtiquetas.set(false);
        this.etiquetandoNegocio.set(null);
        this.vocabulario.set([]);
        this.toast.sucesso('Etiquetas atualizadas.');
        // O negócio traz os chips dentro do detalhe; o contato tem consulta própria.
        if (negocio) this.carregar(); else this.carregarEtiquetas();
      },
      // O modal fica ABERTO com a escolha: a mensagem do servidor distingue "passou do limite" de
      // "alguma não existe mais", e fechar obrigaria a remarcar tudo.
      error: e => {
        this.salvandoEtiquetas.set(false);
        this.erroEtiquetas.set(e.error?.erro ?? 'Não foi possível salvar as etiquetas.');
      }
    });
  }

  /** ⚠️ GANHOU UM QUARTO ESTADO NO E6, e sem ele a tela mentia: o lead que chega pela caixa não
   *  tem negociação nenhuma, e a versão antiga caía em `'aberto'` — mostrando "Registrar venda"
   *  para quem não tem negócio para fechar. O clique levava 409 com "este contato não tem negócio
   *  em aberto", que é a API dizendo o que a tela já deveria saber.
   *
   *  `sem-negocio` é a AUSÊNCIA de etapa, não um carimbo: é o único dos quatro que se lê da
   *  presença do funil em vez do estado dele. */
  situacao = computed<'sem-negocio' | 'ganho' | 'perdido' | 'aberto'>(() => {
    const c = this.contato();
    if (c && c.etapaId === null) return 'sem-negocio';
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

    if (this.auth.ehDono()) {
      this.equipe.listar().subscribe({ next: us => this.equipeLista.set(us), error: () => { } });
    }
  }

  /** As etapas DO FUNIL DO CONTATO, para o seletor.
   *
   *  ⚠️ DEPOIS do detalhe, e não em paralelo: é dele que sai a pipeline. Antes a tela chamava
   *  `quadro(1)` no `ngOnInit` — escrito quando o primeiro parâmetro era `porColuna` e `1`
   *  queria dizer "um card por coluna". Quando `pipeline` entrou na FRENTE da assinatura, a
   *  chamada continuou compilando com outro significado: pedir a pipeline de id 1.
   *
   *  Funcionava por acidente na primeira empresa, cuja pipeline É a de id 1. Nas outras o
   *  seletor vinha VAZIO — sem erro, sem log, e sem como mover o contato de etapa.
   *
   *  `porColuna: 1` porque esta tela quer os NOMES das colunas, não os cards. */
  private carregarEtapasDosFunis(negocios: NegocioDoContato[]) {
    // ⚠️ `?? []` NÃO É PARANOIA. Um payload sem `negocios` derruba esta linha, e o erro sobe
    // até matar a TELA inteira — nos testes o sintoma foi o browser desconectar, que é o mesmo
    // modo de falha que já custou uma investigação neste projeto. Uma lista vazia degrada para
    // "nenhum negócio"; uma exceção degrada para nada na tela.
    const funis = [...new Set((negocios ?? []).map(n => n.pipelineId))];
    if (funis.length === 0) { this.etapasPorFunil.set({}); return; }

    // Falha em silêncio por funil: um seletor vazio é ruim, a tela não abrir é pior.
    forkJoin(funis.map(id => this.etapasApi.listar(id).pipe(catchError(() => of([]))))).subscribe({
      next: listas => this.etapasPorFunil.set(
        Object.fromEntries(funis.map((id, i) => [id, listas[i]]))),
      error: () => { }
    });
  }

  carregar() {
    this.carregando.set(true);
    this.carregarEtiquetas();
    this.servico.detalhe(this.id()).subscribe({
      next: d => {
        this.dados.set(d);
        this.carregando.set(false);
        this.erro.set('');
        this.carregarEtapasDosFunis(d.negocios);
      },
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
      // ⚠️ DOIS FATOS DIFERENTES (E6), e a distincao e a que o vendedor ja fazia na cabeca:
      // `Abriu` e negocio NOVO — o lead da caixa que virou oportunidade, ou o cliente que voltou
      // para a segunda compra. `Reabriu` e a perda desfeita: a mesma negociacao volta ao quadro,
      // na etapa onde tinha morrido.
      case 'Abriu': return 'abriu uma negociação';
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
  /** ⚠️ MOVE A NEGOCIAÇÃO DA LINHA, e antes mandava o id do CONTATO.
   *
   *  `funil.mover` sempre esperou `negociacaoId` — o comentário no serviço avisa que "os dois são
   *  `number`: trocar um pelo outro compila". Esta tela trocava desde o E4c/2: dava
   *  "Negócio não encontrado" a cada mudança de etapa, e num banco onde as faixas de id se
   *  cruzassem teria movido o card de OUTRA pessoa.
   *
   *  A `versao` (o `xmin` do card) vai junto: é o que faz a API recusar com 409 quando alguém
   *  mexeu no card entre esta tela carregar e o clique. Sem ela, a tela do contato seria a porta
   *  sem trava e o último a clicar venceria em silêncio. */
  moverNegocio(negocio: NegocioDoContato, destino: number) {
    if (!destino || destino === negocio.etapaId) return;

    if (this.etapasDe(negocio.pipelineId).find(e => e.id === destino)?.eGanho) {
      this.abrirFechamento('ganho', negocio);
      return;
    }

    this.funil.mover(negocio.id, destino, null, negocio.versao).subscribe({
      next: () => { this.toast.sucesso('Negócio movido.'); this.carregar(); },
      error: e => {
        this.toast.erro(e.error?.erro ?? 'Não foi possível mover o negócio.');
        this.carregar();   // devolve o select ao valor real
      }
    });
  }

  // ---------------------------------------------------------------- fechamento
  /** ⚠️ O MODAL PRECISA SABER QUAL NEGÓCIO FECHA. Sem isso, a API escolhe "o aberto mais
   *  recente" — uma resposta enquanto a pessoa tinha um negócio só, um sorteio desde que ela
   *  pode estar em Vendas e em Pós-venda. Com a lista na tela, o botão de uma linha fecharia o
   *  negócio da outra. */
  fechandoNegocio = signal<NegocioDoContato | null>(null);

  abrirFechamento(tipo: TipoFechamento, negocio?: NegocioDoContato) {
    this.erroFechamento.set('');
    this.fechandoNegocio.set(negocio ?? null);
    this.fechamento.set(tipo);
    if (tipo === 'ganho') this.carregarCanais();
  }

  cancelarFechamento() {
    this.fechamento.set(null);
    this.fechandoNegocio.set(null);
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

    const negocioId = this.fechandoNegocio()?.id ?? null;

    const chamada = r.tipo === 'ganho'
      ? this.servico.marcarGanho(this.id(), r.valor, r.canalId, negocioId)
      : this.servico.marcarPerdido(this.id(), r.motivo, negocioId);

    chamada.subscribe({
      next: () => {
        this.salvandoFechamento.set(false);
        this.fechamento.set(null);
        this.fechandoNegocio.set(null);
        this.toast.sucesso(r.tipo === 'ganho' ? 'Venda registrada.' : 'Negócio marcado como perdido.');
        this.carregar();
      },
      error: e => {
        this.salvandoFechamento.set(false);
        this.erroFechamento.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  /** O funil escolhido no seletor. `null` = "escolha por mim" — o servidor revive a perda, ou
   *  usa o funil do último negócio ganho, ou o padrão. */
  funilEscolhido = signal<number | null>(null);

  abrirNegociacao() {
    this.servico.abrirNegociacao(this.id(), this.funilEscolhido()).subscribe({
      next: () => {
        this.funilEscolhido.set(null);
        this.toast.sucesso('Negociação aberta.');
        this.carregar();
      },
      error: (e: { error?: { erro?: string } }) =>
        this.toast.erro(e.error?.erro ?? 'Não foi possível abrir a negociação.')
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
