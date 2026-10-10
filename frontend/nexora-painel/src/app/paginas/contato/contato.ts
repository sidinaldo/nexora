import { catchError, forkJoin, of } from 'rxjs';
import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ContatosServico, CorpoContato } from '../../nucleo/servicos/contatos.servico';
import { FunilServico } from '../../nucleo/servicos/funil.servico';
import { EtapasServico } from '../../nucleo/servicos/etapas.servico';
import { MeuDiaServico } from '../../nucleo/servicos/meu-dia.servico';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { VendasServico } from '../../nucleo/servicos/vendas.servico';
import { NotaDaCompra, PesquisaNpsServico } from '../../nucleo/servicos/pesquisa-nps.servico';
import { TrilhaServico } from '../../nucleo/servicos/trilha.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { EtiquetasServico } from '../../nucleo/servicos/etiquetas.servico';
import { SeletorEtiquetas } from '../../nucleo/etiquetas/seletor-etiquetas';
import { textoSobre } from '../../nucleo/cor';
import {
  ColunaFunil, ContatoDetalhe, ConversaDoContato, EtapaConfigDto, EtiquetaDto, EventoTrilha, LembreteDto, NegocioDoContato, OrigemLead, UsuarioEquipe, VendaDto, ResumoCompras
} from '../../nucleo/modelos';
import { Thread } from '../../nucleo/thread/thread';
import { POR_PAGINA, Paginacao, rolarParaTopoDaTabela } from '../../nucleo/paginacao/paginacao';
import {
  ModalFechamento, OpcaoCanal, ResultadoFechamento, TipoFechamento
} from '../../nucleo/fechamento/modal-fechamento';
import {
  ModalCancelamento, ResultadoCancelamento
} from '../../nucleo/cancelamento/modal-cancelamento';
import { TetosServico } from '../../nucleo/servicos/tetos.servico';
import { rotuloOrigem, rotuloStatusLembrete } from '../../nucleo/rotulos';
import { erroAo } from '../../nucleo/erros';

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
  imports: [FormsModule, DatePipe, RouterLink, Thread, ModalFechamento, ModalCancelamento,
            Paginacao, SeletorEtiquetas],
  templateUrl: './contato.html',
  styleUrl: './contato.css'
})
export class Contato implements OnInit {
  readonly rotuloOrigem = rotuloOrigem;
  readonly rotuloStatusLembrete = rotuloStatusLembrete;
  private servico = inject(ContatosServico);
  private funil = inject(FunilServico);
  private etapasApi = inject(EtapasServico);
  private lembretesApi = inject(MeuDiaServico);
  private equipe = inject(EquipeServico);
  private vendasApi = inject(VendasServico);
  private pesquisaApi = inject(PesquisaNpsServico);
  private trilhaApi = inject(TrilhaServico);
  private toast = inject(ToastServico);
  private rota = inject(ActivatedRoute);
  private router = inject(Router);
  auth = inject(AuthServico);

  readonly origens: OrigemLead[] = [
    'whatsapp', 'instagram', 'facebook', 'google', 'site', 'qrcode', 'indicacao', 'meta_ads',
    'manual', 'outro'
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

  /** ===================== OS FUNIS LIVRES, PRONTOS DO SERVIDOR =====================
   *  Os funis onde esta pessoa ainda NÃO aparece no quadro — os únicos em que "Abrir negociação"
   *  pode dar certo. A tela não oferece o clique que sempre erra.
   *
   *  ⚠️ ERA CALCULADO AQUI, a partir dos negócios e da lista do menu, e a caixa tinha a sua
   *  própria conta. A versão estreita (só `aberta`) já tinha passado por esta linha uma vez: a
   *  tela oferecia o funil onde a pessoa tinha uma venda esperando conclusão, e o clique voltava
   *  409. Agora o servidor manda a lista (`RegrasNegociacao.OcupaOFunil`), a mesma da caixa. */
  funisDisponiveis = computed(() => this.dados()?.funisDisponiveis ?? []);
  equipeLista = signal<UsuarioEquipe[]>([]);

  // edição
  editando = signal(false);
  salvando = signal(false);
  erroEdicao = signal('');
  /** A venda que está sendo cancelada, enquanto o modal do porquê está aberto (CAN-1). */
  cancelandoVenda = signal<VendaDto | null>(null);
  erroCancelamento = signal('');

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

  /** CONV-XX: uma conversa por número, a principal primeiro (ordem do servidor). */
  conversas = computed(() => this.dados()?.conversas ?? []);
  /** A aba escolhida. Nula = a principal; e se a escolhida sumir num recarregamento, volta a ela. */
  conversaEscolhida = signal<number | null>(null);
  conversaAberta = computed<ConversaDoContato | null>(() => {
    const lista = this.conversas();
    return lista.find(v => v.id === this.conversaEscolhida()) ?? lista[0] ?? null;
  });
  anonimizado = computed(() => !!this.dados()?.anonimizadoEm);

  // ---------------------------------------------------------------- etiquetas
  private etiquetasApi = inject(EtiquetasServico);

  textoSobre = textoSobre;

  /** As do contato e o vocabulário inteiro. Separados de propósito: o vocabulário só é buscado
   *  quando o seletor abre — a tela de contato não precisa dele para desenhar os chips. */
  etiquetas = signal<EtiquetaDto[]>([]);
  vocabulario = signal<EtiquetaDto[]>([]);
  /** O teto de etiquetas por negócio, do servidor (AUD-XX) — o seletor tinha um 8 copiado. */
  etiquetasPorNegocio = signal(0);
  private tetosApi = inject(TetosServico);
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
    // O teto de etiquetas por negócio vem do servidor (AUD-XX), junto do vocabulário.
    this.tetosApi.obter().subscribe({
      next: t => this.etiquetasPorNegocio.set(t.etiquetasPorNegocio),
      error: () => { }
    });
  }

  /** ⚠️ NULO = as etiquetas da PESSOA; preenchido = as DAQUELE negócio. São duas tabelas porque
   *  respondem a perguntas diferentes: "Revendedor" vale em todos os negócios dela, "Urgente"
   *  vale num card só. */
  etiquetandoNegocio = signal<NegocioDoContato | null>(null);

  /** ===================== O QUE O MODAL MOSTRA MARCADO =====================
   *  O seletor e UM so para duas coisas diferentes, e PRECISA olhar quem esta sendo etiquetado.
   *
   *  ⚠️ ISTO ERA `etiquetas()` FIXO — as da PESSOA —, inclusive quando o modal abria para um
   *  NEGOCIO. E o estrago nao era so "nao vem marcado": o seletor salva o que esta marcado, entao
   *  confirmar TROCAVA as etiquetas do negocio pelas do contato. Quem tinha "Urgente" no negocio e
   *  "VIP" na pessoa abria, clicava em salvar, e saia com "VIP" no negocio e "Urgente" perdido.
   *
   *  O `confirmarEtiquetas` sempre soube distinguir os dois (ele escolhe entre
   *  `aplicarNaNegociacao` e `aplicar`); era so a leitura que nao. */
  etiquetasAtuais = computed(() => this.etiquetandoNegocio()?.etiquetas ?? this.etiquetas());

  abrirEtiquetasDoNegocio(negocio: NegocioDoContato) {
    this.erroEtiquetas.set('');
    this.etiquetandoNegocio.set(negocio);
    this.selecionandoEtiquetas.set(true);
    this.etiquetasApi.listar().subscribe({
      next: l => this.vocabulario.set(l),
      error: () => { }
    });
    // O teto de etiquetas por negócio vem do servidor (AUD-XX), junto do vocabulário.
    this.tetosApi.obter().subscribe({
      next: t => this.etiquetasPorNegocio.set(t.etiquetasPorNegocio),
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

  // ⚠️ AQUI HAVIA UM `situacao` COMPUTADO, com a regra VELHA — `ganhoEm` antes de "tem negócio
  // aberto?" — e sem uso nenhum no template desde que a tela passou a listar os negócios um por
  // linha. Código morto com uma regra de negócio errada dentro é a cópia que alguém religa sem
  // saber. A situação agora vem pronta do servidor em `contato().situacao`
  // (`RegrasNegociacao.Situacao`).

  /** Os PENDENTES vêm inteiros no detalhe — o servidor já manda só eles (AUD-XX). */
  lembretesPendentes = computed(() => this.dados()?.lembretes ?? []);

  /** ===================== OS RESOLVIDOS PAGINAM NO SERVIDOR (AUD-XX) =====================
   *  Eram filtrados e paginados aqui, sobre a lista inteira que vinha no detalhe, e o "N
   *  concluídos" era o tamanho dela. Agora a tela pede UMA página e recebe o total contado no
   *  banco. Os pendentes são a lista acionável e aparecem inteiros — ver o template.
   *  ==================================================================================== */
  feitosVisiveis = signal<LembreteDto[]>([]);
  totalFeitos = signal(0);
  totalPaginasFeitos = signal(1);
  paginaFeitos = signal(1);
  @ViewChild('listaFeitos') private listaFeitos?: ElementRef<HTMLElement>;

  irParaFeitos(p: number) {
    this.paginaFeitos.set(p);
    this.carregarFeitos();
    rolarParaTopoDaTabela(this.listaFeitos?.nativeElement);
  }

  private carregarFeitos() {
    this.lembretesApi.resolvidosDoContato(this.id(), this.paginaFeitos(), POR_PAGINA).subscribe({
      next: p => {
        // Página que deixou de existir volta para a última que existe, pelo total do servidor.
        if (p.itens.length === 0 && p.totalCount > 0 && this.paginaFeitos() > p.totalPaginas) {
          this.paginaFeitos.set(p.totalPaginas);
          this.carregarFeitos();
          return;
        }
        this.feitosVisiveis.set(p.itens);
        this.totalFeitos.set(p.totalCount);
        this.totalPaginasFeitos.set(p.totalPaginas);
      },
      error: () => {
        this.feitosVisiveis.set([]);
        this.totalFeitos.set(0);
        this.totalPaginasFeitos.set(1);
      }
    });
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

    if (this.auth.pode('gerenciar_equipe')) {
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

  /** `mostrarCarregando` falso = atualização em silêncio (BUG-XX): a conversa avisa a cada envio,
   *  e trocar a página inteira por "Carregando…" recriava a conversa, piscava a tela e jogava a
   *  rolagem para o topo. */
  carregar(mostrarCarregando = true) {
    if (mostrarCarregando) this.carregando.set(true);
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
      next: h => {
        this.vendas.set(h.vendas);
        this.resumoVendas.set(h.resumo);
      },
      error: () => {
        this.vendas.set([]);
        this.resumoVendas.set(null);
      }
    });

    // O histórico de notas (NPS-1 3.5), pelo mesmo motivo: complemento, e o caso comum é vazio.
    this.pesquisaApi.doContato(this.id()).subscribe({
      next: n => this.notas.set(n),
      error: () => this.notas.set([])
    });

    // Só quem pode ver: pedir e receber 403 encheria o console de erro a cada abertura de
    // contato. A regra que VALE é a do servidor; esta só evita o pedido inútil.
    this.carregarFeitos();

    if (this.auth.pode('ver_historico')) {
      this.carregarTrilha();
    }
  }

  // ---------------------------------------------------------------- trilha (AUD-1)
  trilha = signal<EventoTrilha[]>([]);

  /** ===================== O HISTÓRICO PAGINA NO SERVIDOR (AUD-XX) =====================
   *  Era uma lista cortada em 50 — até 200 com "Ver histórico completo" —, paginada aqui. Um
   *  cliente de anos tinha história que nenhum botão alcançava, e a tela dizia "50 eventos" sobre
   *  o que tinha na mão. Agora cada página vem do servidor, com o total contado no banco: a última
   *  página chega ao começo da história, e o número é o de verdade.
   *  ================================================================================== */
  totalTrilha = signal(0);
  totalPaginasTrilha = signal(1);
  paginaTrilha = signal(1);
  @ViewChild('listaTrilha') private listaTrilha?: ElementRef<HTMLElement>;

  irParaTrilha(p: number) {
    this.paginaTrilha.set(p);
    this.carregarTrilha();
    rolarParaTopoDaTabela(this.listaTrilha?.nativeElement);
  }

  private carregarTrilha() {
    this.trilhaApi.doContato(this.id(), this.paginaTrilha(), POR_PAGINA).subscribe({
      next: p => {
        this.trilha.set(p.itens);
        this.totalTrilha.set(p.totalCount);
        this.totalPaginasTrilha.set(p.totalPaginas);
      },
      error: () => {
        this.trilha.set([]);
        this.totalTrilha.set(0);
        this.totalPaginasTrilha.set(1);
      }
    });
  }

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

  // ---------------------------------------------------------------- notas da pesquisa (NPS-1 3.5)
  notas = signal<NotaDaCompra[]>([]);

  /** O que a pesquisa daquela compra deu, em palavras. Uma frase por estado, e não o nome do
   *  enum: "Expirada" não diz ao vendedor que o cliente simplesmente não respondeu.
   *
   *  ⚠️ `PossivelNota` NÃO MOSTRA O NÚMERO COMO NOTA: é suspeita, e a frase diz onde decidir. */
  situacaoDaNota(n: NotaDaCompra): string {
    if (n.status === 'Agendada') return `pergunta sai em ${Contato.diaMes(n.dataAgendada)}`;
    if (n.status === 'Enviada') return 'perguntado, esperando a resposta';
    if (n.status === 'PossivelNota') return `em dúvida (parece ${n.nota}) — decida na conversa`;
    // A expirada COM nota é a dúvida que ninguém decidiu: o cliente respondeu, e "não respondeu"
    // seria falso.
    if (n.status === 'Expirada') return n.nota === null ? 'não respondeu' : `expirou em dúvida (parecia ${n.nota})`;
    if (n.status === 'Cancelada') return 'pesquisa cancelada';
    return '';
  }

  /** `yyyy-MM-dd` (o `DateOnly` do servidor) em "10/10", partindo a string: `new Date('2026-10-10')`
   *  é meia-noite em UTC, que em Brasília ainda é dia 9. */
  private static diaMes(iso: string): string {
    return `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;
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
   *  lead novo — exatamente a confusão que este bloco veio desfazer.
   *
   *  ⚠️ VEM PRONTO DO SERVIDOR (AUD-XX). Era contado aqui, sobre a lista, e a Caixa fazia a
   *  mesma conta noutra cópia. A regra acima mora em `IServicoVendas.ResumoDoContatoAsync`. */
  resumoVendas = signal<ResumoCompras | null>(null);

  /** ===================== SAIU O `confirm` DO NAVEGADOR (CAN-1) =====================
   *  Ele servia quando cancelar era uma coisa só e a pergunta era "tem certeza?". Agora a pergunta
   *  é OUTRA — *por quê* —, e a resposta muda o que acontece com o card e com o relatório de
   *  perdas. Isso não cabe num diálogo de sim/não, e um `confirm` com duas opções não existe.
   *
   *  O atrito continua: o modal é um passo a mais, e cancelar segue sendo ação de gestor sobre
   *  número fechado.
   *  ============================================================================= */
  cancelarVenda(v: VendaDto) {
    this.erroCancelamento.set('');
    this.cancelandoVenda.set(v);
  }

  fecharCancelamento() {
    this.cancelandoVenda.set(null);
    this.erroCancelamento.set('');
  }

  confirmarCancelamento(r: ResultadoCancelamento) {
    const venda = this.cancelandoVenda();
    if (!venda) return;

    this.cancelando.set(venda.id);
    this.erroCancelamento.set('');

    this.vendasApi.cancelar(venda.id, r.motivo).subscribe({
      next: () => {
        this.cancelando.set(null);
        this.cancelandoVenda.set(null);
        // ⚠️ A FRASE DIZ QUAL DAS DUAS ACONTECEU. "Venda cancelada" para os dois casos esconderia
        //    justamente a diferença que o modal acabou de pedir para a pessoa escolher.
        this.toast.sucesso(r.motivo ? 'Venda cancelada e contada como perda.' : 'Venda cancelada.');
        this.carregar();   // o carimbo do contato pode ter mudado junto
      },
      error: e => {
        this.cancelando.set(null);
        // O modal FICA ABERTO com o erro, como o de fechamento: fechá-lo faria a pessoa digitar o
        // motivo de novo por causa de uma falha que não foi dela.
        this.erroCancelamento.set(e.error?.erro ?? 'Não foi possível cancelar a venda.');
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
        this.erroEdicao.set(erroAo(e, 'salvar o contato'));
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
        this.erroFechamento.set(erroAo(e, r.tipo === 'ganho' ? 'registrar a venda' : 'registrar a perda'));
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
        this.toast.erro(erroAo(e, 'apagar os dados pessoais'));
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
      error: e => this.toast.erro(erroAo(e, 'concluir o lembrete'))
    });
  }

  cancelarLembrete(l: LembreteDto) {
    this.lembretesApi.cancelar(l.id).subscribe({
      next: () => { this.toast.info('Lembrete cancelado.'); this.carregar(); },
      error: e => this.toast.erro(erroAo(e, 'cancelar o lembrete'))
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
