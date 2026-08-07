import { Component, ElementRef, ViewChild, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FunilServico } from '../../nucleo/servicos/funil.servico';
import { ContatosServico } from '../../nucleo/servicos/contatos.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { ColunaFunil, ContatoCard } from '../../nucleo/modelos';
import { ModalFechamento, ResultadoFechamento } from '../../nucleo/fechamento/modal-fechamento';
import {
  JANELA_PADRAO, JanelaAtendimento, Urgencia, janelaDoStatus, urgenciaDe
} from '../../nucleo/semaforo';

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
  imports: [ModalFechamento],
  templateUrl: './funil.html',
  styleUrl: './funil.css'
})
export class Funil implements OnInit, OnDestroy {
  private servico = inject(FunilServico);
  private contatos = inject(ContatosServico);
  private painel = inject(PainelServico);
  private toast = inject(ToastServico);
  private router = inject(Router);
  private rota = inject(ActivatedRoute);

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
  arrastando = signal<ContatoCard | null>(null);

  /** O contêiner que rola na horizontal — a rolagem de borda precisa dele. */
  @ViewChild('quadro') private quadroEl?: ElementRef<HTMLElement>;
  colunaOrigem: number | null = null;
  alvo = signal<Alvo | null>(null);

  // Modal de venda ganha (aberto ao soltar na coluna de ganho, ou pelo menu do card).
  fechando = signal<ContatoCard | null>(null);
  salvandoFechamento = signal(false);
  erroFechamento = signal('');

  // Semáforo: faixas e janela vêm do servidor; quem PINTA é o cliente.
  amareloMin = signal(60);
  vermelhoMin = signal(240);
  janela = signal<JanelaAtendimento>(JANELA_PADRAO);
  private agora = signal(new Date());
  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit() {
    const pedida = Number(this.rota.snapshot.queryParamMap.get('etapa') ?? 0);
    if (pedida > 0) this.etapaDestacada.set(pedida);

    this.carregar();
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

  carregar() {
    this.carregando.set(true);
    this.servico.quadro(this.porColuna).subscribe({
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
    if (coluna.eGanho) { this.fechando.set(card); return; }

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
  }

  cancelarFechamento() {
    // Cancelar depois de arrastar para a coluna de ganho: o card NUNCA saiu do lugar (o
    // movimento otimista não roda nesse caminho), então não há nada para desfazer.
    this.fechando.set(null);
    this.erroFechamento.set('');
  }

  confirmarFechamento(r: ResultadoFechamento) {
    const card = this.fechando();
    if (!card) return;

    this.salvandoFechamento.set(true);
    this.erroFechamento.set('');

    this.contatos.marcarGanho(card.id, r.valor).subscribe({
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
}
