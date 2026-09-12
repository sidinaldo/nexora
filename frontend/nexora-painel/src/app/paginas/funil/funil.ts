import { Component, ElementRef, ViewChild, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { PipelinesServico } from '../../nucleo/servicos/pipelines.servico';
import { EtiquetasServico } from '../../nucleo/servicos/etiquetas.servico';
import { EtapasServico } from '../../nucleo/servicos/etapas.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { SeletorEtiquetas } from '../../nucleo/etiquetas/seletor-etiquetas';
import { textoSobre } from '../../nucleo/cor';
import { FunilServico } from '../../nucleo/servicos/funil.servico';
import { ContatosServico } from '../../nucleo/servicos/contatos.servico';
import { VendasServico } from '../../nucleo/servicos/vendas.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { ColunaFunil, ContatoCard, EtiquetaDto } from '../../nucleo/modelos';
import { ModalFechamento, OpcaoCanal, ResultadoFechamento }
  from '../../nucleo/fechamento/modal-fechamento';
import {
  JANELA_PADRAO, JanelaAtendimento, Urgencia, janelaDoStatus, urgenciaDe
} from '../../nucleo/semaforo';
import { ehCelular } from '../../nucleo/viewport';

/** Onde o card está sendo solto: a coluna e o card imediatamente ACIMA do ponto. */
interface Alvo { etapaId: number; aposContatoId: number | null; }

/** O FUNIL KANBAN.
 *
 *  ===================== ARRASTAR SEM BIBLIOTECA =====================
 *  HTML5 drag-and-drop nativo. O projeto não usa biblioteca de componentes e não começa aqui.
 *
 *  O truque que faz o nativo funcionar bem: a zona de soltura NÃO é o card, é o ESPAÇO ENTRE
 *  cards. Cada coluna tem separadores invisíveis de 8px que engordam ao passar o cursor,
 *  mostrando exatamente onde o card vai cair. Usar o card como alvo obriga a adivinhar "caiu na
 *  metade de cima ou de baixo?", que erra o tempo todo perto das bordas.
 *  ===================================================================
 *
 *  ===================== ATUALIZAÇÃO OTIMISTA =====================
 *  O card se move na tela na hora e a chamada vai em paralelo. Se a API recusar, o card volta
 *  para a posição original e um toast diz por quê. Sem isso, arrastar tem meio segundo de
 *  latência e o vendedor sente — é a diferença entre parecer um app e parecer um formulário.
 *  ================================================================ */
@Component({
  selector: 'app-funil',
  imports: [ModalFechamento, SeletorEtiquetas, FormsModule],
  templateUrl: './funil.html',
  styleUrl: './funil.css'
})
export class Funil implements OnInit, OnDestroy {
  private servico = inject(FunilServico);
  private contatos = inject(ContatosServico);
  private vendas = inject(VendasServico);
  private painel = inject(PainelServico);
  private toast = inject(ToastServico);
  private router = inject(Router);
  private rota = inject(ActivatedRoute);
  private pipelines = inject(PipelinesServico);

  readonly porColuna = 50;

  /** Etapa que veio em `?etapa=` — o dashboard manda ao clicar numa faixa do funil.
   *
   *  DESTACA e ROLA até ela, em vez de esconder as outras. O quadro é um kanban: sua utilidade
   *  está em ver as etapas lado a lado, e "filtrar" escondendo colunas transformaria a tela em
   *  uma lista com passos extras para voltar ao normal. */
  etapaDestacada = signal<number | null>(null);

  colunas = signal<ColunaFunil[]>([]);
  carregando = signal(true);
  erro = signal('');
  carregandoMais = signal<number | null>(null);

  // Arrasto em andamento.
  /** Qual funil este quadro está mostrando. `null` = a padrão, decidida pelo servidor — é o que
   *  acontece em `/crm` sem id. */
  pipeline = signal<number | null>(null);

  /** O nome que vai no título.
   *
   *  ⚠️ VEM DA LISTA DO MENU, não do quadro: `QuadroFunil` só devolve colunas e não sabe de qual
   *  pipeline é. Buscar o nome numa requisição própria seria uma ida ao servidor para um dado que
   *  a lateral já carregou no boot.
   *
   *  O "Funil" de reserva cobre dois instantes reais: a lista ainda não chegou, ou o menu falhou
   *  ao carregar (o erro dele é engolido de propósito). Título genérico é pior que o nome certo e
   *  melhor que título vazio. */
  nomeDaPipeline = computed(() => {
    const lista = this.pipelines.lista();
    const id = this.pipeline();
    const alvo = id === null ? lista.find(p => p.padrao) : lista.find(p => p.id === id);
    return alvo?.nome ?? 'Funil';
  });

  arrastando = signal<ContatoCard | null>(null);

  /** O contêiner que rola na horizontal — a rolagem de borda precisa dele. */
  @ViewChild('quadro') private quadroEl?: ElementRef<HTMLElement>;
  colunaOrigem: number | null = null;
  alvo = signal<Alvo | null>(null);

  // Modal de venda ganha (aberto ao soltar na coluna de ganho, ou pelo menu do card).
  fechando = signal<ContatoCard | null>(null);

  // ---------------------------------------------------------------- criar etapa (issue #7)
  /** ⚠️ POR `auth.ehDono()`, E NÃO PELO GUARD DA ROTA. `/crm` é de TODO papel — é o quadro, a
   *  operação diária —, enquanto `/crm/:pipeline/etapas` tem `guardaDono`. Aqui não há rota para
   *  proteger: é um controle dentro de uma tela que o vendedor também abre.
   *
   *  O enforcement continua no servidor: `EtapasController` inteiro é `Roles = "dono"`. Isto aqui
   *  é para o vendedor não ver um botão que lhe daria 403. */
  private auth = inject(AuthServico);
  private etapasApi = inject(EtapasServico);

  /** Espelha `ServicoEtapas.MaximoEtapas`. Duplicado para a tela desabilitar ANTES de o dono
   *  digitar um nome e levar 400. */
  readonly maximoEtapas = 12;

  criandoEtapa = signal(false);
  criandoSalvando = signal(false);
  erroNovaEtapa = signal('');
  fNomeEtapa = signal('');
  fCorEtapa = signal('#5C8F6E');

  ehDono = this.auth.ehDono;
  funilCheio = computed(() => this.colunas().length >= this.maximoEtapas);

  abrirNovaEtapa() {
    this.fNomeEtapa.set('');
    this.fCorEtapa.set('#5C8F6E');
    this.erroNovaEtapa.set('');
    this.criandoEtapa.set(true);
  }

  cancelarNovaEtapa() {
    this.criandoEtapa.set(false);
    this.erroNovaEtapa.set('');
  }

  criarEtapa() {
    const nome = this.fNomeEtapa().trim();
    if (this.criandoSalvando()) return;
    if (nome.length < 2) { this.erroNovaEtapa.set('Dê um nome à etapa.'); return; }

    this.criandoSalvando.set(true);
    this.erroNovaEtapa.set('');

    // ⚠️ A pipeline ATUAL, não a padrão. Sem ela a etapa nasceria no funil errado — e o dono só
    // descobriria ao trocar de pipeline e ver uma coluna que não pediu.
    this.etapasApi.criar(nome, this.fCorEtapa(), this.pipeline()).subscribe({
      next: () => {
        this.criandoSalvando.set(false);
        this.criandoEtapa.set(false);
        this.toast.sucesso(`Etapa "${nome}" criada.`);
        // O quadro inteiro: a coluna nova não existe para recarregar sozinha.
        this.carregar();
      },
      // O formulário fica ABERTO com o que foi digitado: a mensagem do servidor distingue "já
      // existe" de "chegou no limite", e fechar obrigaria a redigitar.
      error: e => {
        this.criandoSalvando.set(false);
        this.erroNovaEtapa.set(e.error?.erro ?? 'Não foi possível criar a etapa.');
      }
    });
  }

  // ---------------------------------------------------------------- etiquetas
  private etiquetasApi = inject(EtiquetasServico);

  textoSobre = textoSobre;

  /** O CARD inteiro, não só o id — o template lê `card.nome` e `card.etiquetas`. Mesmo desenho de
   *  `fechando` logo acima, e pelo mesmo motivo. */
  etiquetando = signal<ContatoCard | null>(null);

  /** ⚠️ A ETAPA vem à parte porque o CARD NÃO A CARREGA — ela é implícita na coluna que o segura.
   *  É o mesmo motivo pelo qual `abrirMover(card, etapaId, evento)` também a recebe. Sem ela, não
   *  há como recarregar só a coluna certa depois de salvar. */
  private etapaDoEtiquetando: number | null = null;
  salvandoEtiquetas = signal(false);
  erroEtiquetas = signal('');
  vocabulario = signal<EtiquetaDto[]>([]);

  abrirEtiquetas(card: ContatoCard, etapaId: number, evento?: Event) {
    // ⚠️ O `<article>` é arrastável e tem um `(click)` que abre o contato. Sem isto, marcar
    // etiqueta abriria o contato por baixo. Mesma primeira linha de `abrirVenda`.
    evento?.stopPropagation();
    this.erroEtiquetas.set('');
    this.etiquetando.set(card);
    this.etapaDoEtiquetando = etapaId;
    this.etiquetasApi.listar().subscribe({
      next: l => this.vocabulario.set(l),
      error: () => { }
    });
  }

  cancelarEtiquetas() {
    this.etiquetando.set(null);
    this.etapaDoEtiquetando = null;
    this.erroEtiquetas.set('');
    this.vocabulario.set([]);
  }

  confirmarEtiquetas(ids: number[]) {
    const card = this.etiquetando();
    if (!card || this.salvandoEtiquetas()) return;

    this.salvandoEtiquetas.set(true);
    this.erroEtiquetas.set('');

    this.etiquetasApi.aplicar(card.id, ids).subscribe({
      next: () => {
        this.salvandoEtiquetas.set(false);
        this.etiquetando.set(null);
        this.vocabulario.set([]);
        this.toast.sucesso('Etiquetas atualizadas.');
        // Recarrega a COLUNA, não o quadro inteiro: só os chips daquele card mudaram, e recarregar
        // tudo perderia a rolagem de todas as outras colunas.
        if (this.etapaDoEtiquetando) this.recarregarColuna(this.etapaDoEtiquetando);
        this.etapaDoEtiquetando = null;
      },
      error: e => {
        this.salvandoEtiquetas.set(false);
        this.erroEtiquetas.set(e.error?.erro ?? 'Não foi possível salvar as etiquetas.');
      }
    });
  }
  salvandoFechamento = signal(false);
  erroFechamento = signal('');
  /** NEG-3 · as campanhas oferecidas no modal, e a que o sistema detectou nesta conversa. */
  canaisFechamento = signal<OpcaoCanal[]>([]);
  canalDetectado = signal<number | null>(null);

  // ================================================================ conclusão (NEG-2)
  /** Contatos marcados para concluir em lote. Só existe na coluna de ganho.
   *
   *  Um `Set` dentro de um signal, e a troca é sempre por INSTÂNCIA NOVA: signal compara por
   *  referência, e mutar o mesmo Set não notificaria ninguém — a caixa marcaria no DOM e a
   *  barra de lote não apareceria. */
  selecionados = signal<ReadonlySet<number>>(new Set());
  concluindo = signal(false);

  selecionado(id: number) { return this.selecionados().has(id); }

  limparSelecao() { this.selecionados.set(new Set()); }

  alternarSelecao(id: number, evento: Event) {
    // O card inteiro é clicável (abre o contato) e arrastável. Sem este `stopPropagation`,
    // marcar a caixa navegaria para fora da tela — e ninguém chegaria a concluir nada.
    evento.stopPropagation();
    this.selecionados.update(atual => {
      const proximo = new Set(atual);
      if (!proximo.delete(id)) proximo.add(id);
      return proximo;
    });
  }

  /** Quantos cards DESTA coluna estão marcados. */
  marcadosNa(col: ColunaFunil): number {
    return col.contatos.reduce((n, c) => n + (this.selecionados().has(c.id) ? 1 : 0), 0);
  }

  concluirCard(card: ContatoCard, evento: Event) {
    evento.stopPropagation();
    this.concluirContatos([card.id]);
  }

  concluirSelecionados(col: ColunaFunil) {
    const ids = col.contatos.filter(c => this.selecionados().has(c.id)).map(c => c.id);
    if (ids.length > 0) this.concluirContatos(ids);
  }

  private concluirContatos(contatoIds: number[]) {
    if (this.concluindo()) return;
    this.concluindo.set(true);

    this.vendas.concluirDoContato(contatoIds).subscribe({
      next: r => {
        this.concluindo.set(false);
        this.limparSelecao();

        // ZERO TEM EXPLICAÇÃO, e ela precisa aparecer: outra pessoa concluiu antes, ou a venda
        // foi cancelada no meio. Um "pronto" silencioso deixaria o card na tela sem motivo.
        if (r.concluidas === 0) {
          this.toast.erro('Nada a concluir — esses pedidos já haviam sido fechados.');
        } else {
          this.toast.sucesso(r.concluidas === 1
            ? 'Pedido concluído. O valor continua no faturamento.'
            : `${r.concluidas} pedidos concluídos. Os valores continuam no faturamento.`);
        }
        // O quadro INTEIRO: o card sai da coluna de ganho e o contador de concluídas sobe.
        this.carregar();
      },
      error: e => {
        this.concluindo.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível concluir.');
      }
    });
  }

  // Semáforo: faixas e janela vêm do servidor; quem PINTA é o cliente.
  amareloMin = signal(60);
  vermelhoMin = signal(240);
  janela = signal<JanelaAtendimento>(JANELA_PADRAO);
  private agora = signal(new Date());
  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit() {
    const pedida = Number(this.rota.snapshot.queryParamMap.get('etapa') ?? 0);
    if (pedida > 0) this.etapaDestacada.set(pedida);

    // ===================== ASSINA `paramMap`, NÃO LÊ `snapshot` =====================
    // Navegar de `/crm/1` para `/crm/2` REUTILIZA este componente — o Angular não o destrói, só
    // troca o parâmetro. Um `snapshot` lido uma vez aqui desenharia o primeiro funil e nunca
    // mais mudaria: trocar de funil no menu não faria nada, sem erro nenhum.
    //
    // Foi exatamente assim que a caixa de entrada quebrou quando passou a usar `?conversa=`, e
    // está registrado em `telas-do-painel.ts`. Este é o segundo caso; não vai haver um terceiro.
    // ===============================================================================
    this.rota.paramMap.subscribe(p => {
      const id = Number(p.get('pipeline') ?? 0);
      this.pipeline.set(id > 0 ? id : null);
      this.limparEstadoDoQuadro();
      this.carregar();
    });

    this.painel.status().subscribe({
      next: s => {
        this.amareloMin.set(s.semaforoAmareloMinutos);
        this.vermelhoMin.set(s.semaforoVermelhoMinutos);
        this.janela.set(janelaDoStatus(s));
      },
      error: () => { }
    });
    this.timer = setInterval(() => this.agora.set(new Date()), 60_000);
  }

  ngOnDestroy() { if (this.timer) clearInterval(this.timer); }

  /** ⚠️ O QUE PRECISA MORRER AO TROCAR DE FUNIL. Como o componente é reutilizado, tudo isto
   *  sobreviveria à troca e passaria a apontar para colunas e cards que não existem mais:
   *
   *    `colunaVisivel` é um ÍNDICE na faixa de abas do celular — 4 num funil de 2 etapas;
   *    `selecionados` guarda ids de contato do funil anterior, e "Concluir" os concluiria;
   *    `arrastando`/`menuMover` deixariam um arrasto ou um menu abertos sobre o quadro novo.
   *
   *  Nada disso dá erro. Dá ação na linha errada, que é pior. */
  private limparEstadoDoQuadro() {
    this.colunas.set([]);
    this.colunaVisivel.set(0);
    this.selecionados.set(new Set());
    this.arrastando.set(null);
    this.menuMover.set(null);
    this.fechando.set(null);
  }

  carregar() {
    this.carregando.set(true);
    this.servico.quadro(this.pipeline(), this.porColuna).subscribe({
      next: q => {
        this.colunas.set(q.colunas);
        this.carregando.set(false);
        this.erro.set('');

        // Rola até a etapa pedida DEPOIS de as colunas existirem no DOM. `setTimeout(0)` porque
        // o signal acabou de mudar e o elemento ainda não foi renderizado.
        const alvo = this.etapaDestacada();
        if (alvo) setTimeout(() => this.rolarAteEtapa(alvo), 0);
      },
      error: () => {
        this.erro.set('Não foi possível carregar o funil.');
        this.carregando.set(false);
      }
    });
  }

  private rolarAteEtapa(etapaId: number) {
    document.getElementById(`etapa-${etapaId}`)
      ?.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
  }

  /** Recarrega UMA coluna do zero. Usado depois de 409 (outro vendedor mexeu no mesmo card) e
   *  quando a renormalização do servidor muda as ordens que temos em mãos. */
  private recarregarColuna(etapaId: number) {
    this.servico.coluna(etapaId, null, null, this.porColuna).subscribe(p => {
      this.colunas.update(cs => cs.map(c => c.etapaId === etapaId
        ? { ...c, contatos: p.itens, temMais: p.temMais }
        : c));
    });
  }

  /** "Carregar mais" da coluna: cursor por (ordemKanban, id) do último card. */
  carregarMais(coluna: ColunaFunil) {
    if (this.carregandoMais() !== null) return;
    const ultimo = coluna.contatos[coluna.contatos.length - 1];
    if (!ultimo) return;

    this.carregandoMais.set(coluna.etapaId);
    this.servico.coluna(coluna.etapaId, ultimo.ordemKanban, ultimo.id, this.porColuna).subscribe({
      next: p => {
        this.colunas.update(cs => cs.map(c => {
          if (c.etapaId !== coluna.etapaId) return c;
          const existentes = new Set(c.contatos.map(x => x.id));
          const novos = p.itens.filter(x => !existentes.has(x.id));
          return { ...c, contatos: [...c.contatos, ...novos], temMais: p.temMais };
        }));
        this.carregandoMais.set(null);
      },
      error: () => this.carregandoMais.set(null)
    });
  }

  // ---------------------------------------------------------------- arrastar
  aoIniciarArrasto(evento: DragEvent, card: ContatoCard, etapaId: number) {
    this.arrastando.set(card);
    this.colunaOrigem = etapaId;
    if (evento.dataTransfer) {
      evento.dataTransfer.effectAllowed = 'move';
      // Alguns navegadores exigem dados no dataTransfer para iniciar o arrasto.
      evento.dataTransfer.setData('text/plain', String(card.id));
    }
  }

  aoTerminarArrasto() {
    this.arrastando.set(null);
    this.alvo.set(null);
    this.profundidade.clear();
    this.colunaOrigem = null;
  }

  /** ===================== O ALVO É A COLUNA INTEIRA (DES-4) =====================
   *  Antes quem escutava eram as tiras `.solta` ENTRE os cards — faixas de poucos pixels. O
   *  espaço vazio abaixo dos cards, que é a maior parte de uma coluna com dois cards, não
   *  escutava nada: o `drop` nunca disparava e o card voltava sozinho, sem erro e sem
   *  explicação. O vendedor tentava, falhava, e concluía que o kanban não funciona.
   *
   *  Agora quem escuta é `.coluna-corpo`, que já ocupa toda a altura (`flex: 1`) — inclusive
   *  vazia. As tiras viraram MARCADOR, com `pointer-events: none`.
   *
   *  ⚠️ `preventDefault` é obrigatório no `dragover` E no `dragenter`. Sem ele o navegador não
   *  considera o elemento uma zona válida e o `drop` NUNCA dispara — em silêncio, sem erro no
   *  console. É a pegadinha nº 1 do DnD nativo, e é contraintuitivo o bastante para alguém
   *  "limpar" isso numa refatoração. Não limpe.
   *  ============================================================================= */
  aoEntrarNaColuna(evento: DragEvent, etapaId: number) {
    if (!this.arrastando()) return;
    evento.preventDefault();

    // CONTADOR DE PROFUNDIDADE: cada card filho dispara `dragenter`/`dragleave` da coluna ao
    // passar por cima. Tratando ingenuamente, o destaque pisca a cada card e o estado se perde.
    this.profundidade.set(etapaId, (this.profundidade.get(etapaId) ?? 0) + 1);
  }

  aoSairDaColuna(etapaId: number) {
    const n = (this.profundidade.get(etapaId) ?? 1) - 1;
    if (n <= 0) {
      this.profundidade.delete(etapaId);
      if (this.alvo()?.etapaId === etapaId) this.alvo.set(null);
    } else {
      this.profundidade.set(etapaId, n);
    }
  }

  aoPassarSobre(evento: DragEvent, etapaId: number) {
    if (!this.arrastando()) return;
    evento.preventDefault();
    if (evento.dataTransfer) evento.dataTransfer.dropEffect = 'move';

    const corpo = evento.currentTarget as HTMLElement;
    const apos = this.pontoDeInsercao(corpo, evento.clientY);

    const atual = this.alvo();
    if (atual?.etapaId !== etapaId || atual?.aposContatoId !== apos) {
      this.alvo.set({ etapaId, aposContatoId: apos });
    }

    this.rolarNasBordas(corpo, evento);
  }

  aoSoltar(evento: DragEvent, coluna: ColunaFunil) {
    evento.preventDefault();

    const card = this.arrastando();
    const aposContatoId = this.pontoDeInsercao(evento.currentTarget as HTMLElement, evento.clientY);

    this.alvo.set(null);
    this.profundidade.clear();
    if (!card) return;

    const origem = this.colunaOrigem;
    this.arrastando.set(null);
    this.colunaOrigem = null;

    // Soltar exatamente onde já estava não é um movimento.
    if (origem === coluna.etapaId && this.posicaoAtual(coluna, card.id) === aposContatoId) return;

    // ===== A ETAPA DE GANHO NÃO É UM MOVIMENTO COMUM =====
    // A API recusa `mover` para etapa com e_ganho — de propósito, não por bug. Abrir o modal
    // aqui é o que faz "arrastar para Venda" e "clicar em venda fechada" serem a mesma coisa.
    // O card só sai do lugar depois de confirmado.
    if (coluna.eGanho) { this.fechando.set(card); this.carregarCanais(card.id); return; }

    this.moverOtimista(card, origem, coluna.etapaId, aposContatoId);
  }

  /** O card imediatamente ACIMA de `id` na coluna (null = ele é o primeiro). */
  private posicaoAtual(coluna: ColunaFunil, id: number): number | null {
    const i = coluna.contatos.findIndex(c => c.id === id);
    return i <= 0 ? null : coluna.contatos[i - 1].id;
  }

  /** Move na tela ANTES da resposta e desfaz se a API recusar. */
  private moverOtimista(
    card: ContatoCard, origemId: number | null, destinoId: number, aposContatoId: number | null
  ) {
    const anterior = this.colunas();   // snapshot para o desfazer

    this.colunas.update(cs => cs.map(c => {
      if (c.etapaId === origemId && origemId !== destinoId) {
        return {
          ...c,
          contatos: c.contatos.filter(x => x.id !== card.id),
          total: c.total - 1,
          valorTotal: c.valorTotal - (card.valor ?? 0)
        };
      }
      if (c.etapaId !== destinoId) return c;

      const sem = c.contatos.filter(x => x.id !== card.id);
      const posicao = aposContatoId === null
        ? 0
        : sem.findIndex(x => x.id === aposContatoId) + 1;
      const lista = [...sem.slice(0, posicao), card, ...sem.slice(posicao)];

      const jaEstava = origemId === destinoId;
      return {
        ...c,
        contatos: lista,
        total: jaEstava ? c.total : c.total + 1,
        valorTotal: jaEstava ? c.valorTotal : c.valorTotal + (card.valor ?? 0)
      };
    }));

    // A versão vai junto: é o que faz dois vendedores arrastando o mesmo card virar um 409
    // explícito em vez de "o último ganha, em silêncio".
    this.servico.mover(card.id, destinoId, aposContatoId, card.versao).subscribe({
      next: r => {
        // A ordem de volta pode divergir do que pintamos se o servidor renormalizou a coluna.
        // Recarregar a coluna alinha os cursores — sem isso o "carregar mais" pediria a partir
        // de uma ordem que não existe mais.
        if (r.ordemKanban !== card.ordemKanban) this.recarregarColuna(destinoId);
      },
      error: e => {
        // DESFAZ e explica. 409 é conflito de estado (outro vendedor mexeu, ou o card virou
        // perdido); qualquer outro erro também devolve o card ao lugar.
        this.colunas.set(anterior);
        this.toast.erro(e.error?.erro ?? 'Não foi possível mover o card.');
        if (e.status === 409) {
          this.recarregarColuna(destinoId);
          if (origemId !== null && origemId !== destinoId) this.recarregarColuna(origemId);
        }
      }
    });
  }

  // ---------------------------------------------------------------- fechamento
  abrirVenda(card: ContatoCard, evento?: Event) {
    evento?.stopPropagation();
    this.erroFechamento.set('');
    this.fechando.set(card);
    this.carregarCanais(card.id);
  }

  /** ⚠️ Falha em silêncio: o canal é opcional e a venda não pode depender dele. */
  private carregarCanais(contatoId: number) {
    this.contatos.canaisDoFechamento(contatoId).subscribe({
      next: r => { this.canaisFechamento.set(r.canais); this.canalDetectado.set(r.detectadoId); },
      error: () => { this.canaisFechamento.set([]); this.canalDetectado.set(null); }
    });
  }

  cancelarFechamento() {
    // Cancelar depois de arrastar para a coluna de ganho: o card NUNCA saiu do lugar (o
    // movimento otimista não roda nesse caminho), então não há nada para desfazer.
    this.fechando.set(null);
    this.erroFechamento.set('');
    this.canaisFechamento.set([]);
    this.canalDetectado.set(null);
  }

  confirmarFechamento(r: ResultadoFechamento) {
    const card = this.fechando();
    if (!card) return;

    this.salvandoFechamento.set(true);
    this.erroFechamento.set('');

    this.contatos.marcarGanho(card.id, r.valor, r.canalId).subscribe({
      next: () => {
        this.salvandoFechamento.set(false);
        this.fechando.set(null);
        this.toast.sucesso(`Venda de ${card.nome} registrada.`);
        // Recarrega o quadro inteiro: o ganho mexe em duas colunas e nos dois totais.
        this.carregar();
      },
      error: e => {
        this.salvandoFechamento.set(false);
        this.erroFechamento.set(e.error?.erro ?? 'Não foi possível registrar a venda.');
      }
    });
  }

  // ---------------------------------------------------------------- apoio
  abrirContato(card: ContatoCard) {
    this.router.navigate(['/contatos', card.id]);
  }

  /** A COR sai do timestamp, no cliente — nunca pedida à API: ela muda com o tempo, e o quadro
   *  precisa envelhecer entre requisições. */
  urgencia(card: ContatoCard): Urgencia {
    return urgenciaDe(
      card.aguardandoDesde, this.amareloMin(), this.vermelhoMin(), this.agora(), this.janela());
  }

  // ---------------------------------------------------------------- onde o card entra
  /** Quantos `dragenter` sem `dragleave` correspondente por coluna. Ver `aoEntrarNaColuna`. */
  private profundidade = new Map<number, number>();

  /** O card DEPOIS do qual o arrastado entra, ou `null` para o topo.
   *
   *  Decide pela METADE do card: cursor acima do meio entra antes, abaixo entra depois. É o que
   *  faz o marcador coincidir com onde o card realmente cai — e soltar no espaço vazio abaixo
   *  de tudo manda para o fim, que é o comportamento esperado.
   *
   *  Lê o DOM em vez do modelo porque a pergunta é geométrica: "em que altura está o cursor". */
  private pontoDeInsercao(corpo: HTMLElement, y: number): number | null {
    const cards = [...corpo.querySelectorAll<HTMLElement>('.card[data-id]')]
      .filter(el => el.dataset['id'] && !el.classList.contains('arrastando'));

    let apos: number | null = null;
    for (const el of cards) {
      const caixa = el.getBoundingClientRect();
      if (y < caixa.top + caixa.height / 2) break;
      apos = Number(el.dataset['id']);
    }
    return apos;
  }

  // ---------------------------------------------------------------- rolagem durante o arrasto
  /** Zona de borda em que a rolagem começa, e o quanto anda por evento. */
  private static readonly BORDA = 56;
  private static readonly PASSO = 14;

  /** Sem isto, mover um card para a última etapa exige rolar ANTES de arrastar — e o card não
   *  pode esperar: soltar para rolar cancela o arrasto.
   *
   *  Vertical na COLUNA (que tem rolagem própria desde o DES-1) e horizontal no QUADRO. */
  private rolarNasBordas(corpo: HTMLElement, evento: DragEvent) {
    const c = corpo.getBoundingClientRect();
    if (evento.clientY - c.top < Funil.BORDA) corpo.scrollTop -= Funil.PASSO;
    else if (c.bottom - evento.clientY < Funil.BORDA) corpo.scrollTop += Funil.PASSO;

    const quadro = this.quadroEl?.nativeElement;
    if (!quadro) return;

    const q = quadro.getBoundingClientRect();
    if (evento.clientX - q.left < Funil.BORDA) quadro.scrollLeft -= Funil.PASSO;
    else if (q.right - evento.clientX < Funil.BORDA) quadro.scrollLeft += Funil.PASSO;
  }

  ehAlvo(etapaId: number, aposContatoId: number | null): boolean {
    const a = this.alvo();
    return a?.etapaId === etapaId && a?.aposContatoId === aposContatoId;
  }

  moeda(v: number | null): string {
    if (v === null || v === undefined) return '';
    return v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
  }

  iniciais(nome: string): string {
    const p = (nome || '').trim().split(/\s+/);
    return ((p[0]?.[0] ?? '') + (p.length > 1 ? p[p.length - 1][0] : '')).toUpperCase() || '?';
  }

  // ==================================================================== celular (MOB-2)
  /** Ver `nucleo/viewport.ts`. */
  protected readonly ehCelular = ehCelular;

  /** ===================== MOVER SEM ARRASTAR =====================
   *  HTML5 drag-and-drop NÃO funciona em toque: `dragstart` simplesmente não dispara, porque o
   *  gesto de arrastar é interpretado como rolagem. O DES-4 mediu isso e registrou o que ficou
   *  faltando — dizer na tela. Hoje o vendedor tenta arrastar, o card não se move, e nada explica.
   *
   *  Um botão VISÍVEL no card responde melhor que um aviso: em vez de contar por que o gesto não
   *  funciona, oferece o que funciona. A pendência do DES-4 fecha por substituição.
   *
   *  ⚠️ REUSA `moverOtimista`. Nada de segundo caminho de escrita: o mesmo movimento na tela antes
   *  da resposta, o mesmo desfazer, a mesma `versao` que transforma dois vendedores mexendo no
   *  mesmo card num 409 explícito, e a mesma recarga de coluna.
   *  ============================================================== */
  menuMover = signal<ContatoCard | null>(null);
  private origemDoMenu: number | null = null;

  abrirMover(card: ContatoCard, etapaId: number, evento: Event) {
    // Sem isto o clique sobe para o `article` e abre o contato em vez do menu.
    evento.stopPropagation();
    this.origemDoMenu = etapaId;
    this.menuMover.set(card);
  }

  ehOrigemDoMenu(etapaId: number) { return this.origemDoMenu === etapaId; }

  moverPara(destino: ColunaFunil) {
    const card = this.menuMover();
    if (!card) return;
    const origem = this.origemDoMenu;
    this.menuMover.set(null);

    if (destino.etapaId === origem) return;

    // ⚠️ A MESMA REGRA DO ARRASTO: a API recusa `mover` para etapa de ganho, de propósito. Aqui,
    // como lá, o caminho é o modal de fechamento — e o card só sai do lugar depois de confirmado.
    if (destino.eGanho) { this.abrirVenda(card); return; }

    // `null` = topo da coluna de destino. Não há ponto de inserção num menu: quem escolhe etapa
    // está movendo de fase, não ordenando dentro dela.
    this.moverOtimista(card, origem, destino.etapaId, null);
  }

  /** ===================== ONDE EU ESTOU NO QUADRO =====================
   *  O quadro rola na horizontal e em 390px cabe uma coluna e um pedaço da próxima. Sem indicador,
   *  o vendedor não tem como saber quantas etapas existem nem em qual está — e o valor do kanban é
   *  justamente enxergar as fases lado a lado.
   *
   *  A coluna "atual" é a mais próxima da borda esquerda, medida no DOM em vez de calculada a
   *  partir da largura da coluna: um número repetido aqui e no CSS diverge na primeira mudança.
   *  ================================================================== */
  colunaVisivel = signal(0);

  aoRolarQuadro() {
    const quadro = this.quadroEl?.nativeElement;
    if (!quadro) return;
    // ⚠️ `.coluna`, e NÃO `children`. Desde a issue #7 o quadro tem um último filho que não é
    // coluna — a faixa tracejada de criar etapa. Com `children`, rolar até o fim escolheria ela e
    // `colunaVisivel` viraria um índice sem aba correspondente: a faixa de etapas do celular
    // ficaria sem nenhuma ativa, sem erro nenhum.
    const colunas = [...quadro.querySelectorAll('.coluna')] as HTMLElement[];
    let melhor = 0, menor = Infinity;
    colunas.forEach((c, i) => {
      const d = Math.abs(c.offsetLeft - quadro.offsetLeft - quadro.scrollLeft);
      if (d < menor) { menor = d; melhor = i; }
    });
    this.colunaVisivel.set(melhor);
  }

  irParaColuna(indice: number) {
    // Mesma razão de `aoRolarQuadro`: o índice vem da faixa de abas, que só conhece colunas.
    const alvo = this.quadroEl?.nativeElement
      .querySelectorAll('.coluna')[indice] as HTMLElement | undefined;
    alvo?.scrollIntoView({ behavior: 'smooth', inline: 'start', block: 'nearest' });
  }
}
