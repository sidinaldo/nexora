import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
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
    this.colunaOrigem = null;
  }

  /** `preventDefault` é OBRIGATÓRIO no dragover: sem ele o navegador não considera o elemento
   *  uma zona de soltura válida e o `drop` nunca dispara. É a pegadinha nº 1 do DnD nativo. */
  aoPassarSobre(evento: DragEvent, etapaId: number, aposContatoId: number | null) {
    if (!this.arrastando()) return;
    evento.preventDefault();
    if (evento.dataTransfer) evento.dataTransfer.dropEffect = 'move';

    const atual = this.alvo();
    if (atual?.etapaId !== etapaId || atual?.aposContatoId !== aposContatoId) {
      this.alvo.set({ etapaId, aposContatoId });
    }
  }

  aoSoltar(evento: DragEvent, coluna: ColunaFunil, aposContatoId: number | null) {
    evento.preventDefault();
    const card = this.arrastando();
    this.alvo.set(null);
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
