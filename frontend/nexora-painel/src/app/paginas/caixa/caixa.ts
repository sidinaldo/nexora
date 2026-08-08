import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { CaixaServico } from '../../nucleo/servicos/caixa.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { VendasServico } from '../../nucleo/servicos/vendas.servico';
import { ContatosServico } from '../../nucleo/servicos/contatos.servico';
import {
  ModalFechamento, OpcaoCanal, ResultadoFechamento
} from '../../nucleo/fechamento/modal-fechamento';
import { ConversaResumo, FiltroConversa } from '../../nucleo/modelos';
import { Thread } from '../../nucleo/thread/thread';
import {
  JANELA_PADRAO, JanelaAtendimento, Urgencia, janelaDoStatus, rotuloEspera, urgenciaDe
} from '../../nucleo/semaforo';

interface Aba { chave: FiltroConversa; rotulo: string; }

/** A CAIXA DE ENTRADA — onde o vendedor atende.
 *
 *  Um contato = uma conversa. Diferente do Recupera, onde a thread era por devedor e o ticket
 *  por dívida (cada credor negociava a sua), aqui não há duas granularidades.
 *
 *  A mecânica da thread (cursor, âncora de rolagem, envio) mora em `nucleo/thread` — o detalhe
 *  do contato mostra a MESMA conversa, e duplicar aquilo significaria consertar cada bug duas
 *  vezes. Esta página cuida da LISTA e do cabeçalho da conversa. */
@Component({
  selector: 'app-caixa',
  imports: [DatePipe, RouterLink, Thread, ModalFechamento],
  templateUrl: './caixa.html',
  styleUrl: './caixa.css'
})
export class Caixa implements OnInit, OnDestroy {
  private servico = inject(CaixaServico);
  private painel = inject(PainelServico);
  private auth = inject(AuthServico);
  private toast = inject(ToastServico);
  private rota = inject(ActivatedRoute);
  private vendasApi = inject(VendasServico);
  private contatosApi = inject(ContatosServico);
  realtime = inject(RealtimeServico);

  readonly abas: Aba[] = [
    { chave: 'Aguardando', rotulo: 'Aguardando resposta' },
    { chave: 'Minhas', rotulo: 'Minhas' },
    { chave: 'NaoAtribuidas', rotulo: 'Não atribuídas' },
    { chave: 'Todas', rotulo: 'Todas' },
    { chave: 'Resolvidas', rotulo: 'Resolvidas' }
  ];

  filtro = signal<FiltroConversa>('Aguardando');
  busca = signal('');

  conversas = signal<ConversaResumo[]>([]);
  temMais = signal(false);
  carregandoMais = signal(false);
  carregandoLista = signal(true);

  // Cursor da lista (NÃO offset): último_mensagem_em + id do último item carregado. A lista
  // se reordena em tempo real, então offset duplicaria ou pularia entre páginas.
  private cursorEm: string | null = null;
  private cursorId: number | null = null;

  sel = signal<ConversaResumo | null>(null);

  // Limites e janela vêm do servidor; quem PINTA é o cliente.
  amareloMin = signal(60);
  vermelhoMin = signal(240);
  janela = signal<JanelaAtendimento>(JANELA_PADRAO);
  /** Tick de relógio: força o recálculo da cor sem novo fetch — a lista envelhece sozinha. */
  private agora = signal(new Date());

  meuId = computed(() => this.auth.usuario()?.id ?? 0);

  private inscricoes: Subscription[] = [];
  private timerAgora: ReturnType<typeof setInterval> | null = null;
  private buscaTimer?: ReturnType<typeof setTimeout>;

  /** O aviso de mensagens recuperadas (REC-1). Sai do MESMO `status()` que o shell já busca em
   *  polling — sem requisição nova, e o aviso aparece sozinho quando as atrasadas entram. */
  recuperacao = computed(() => this.painel.ultimo()?.recuperacao ?? null);

  /** "ontem, 14h20 às 16h05" — o período em que o cliente escreveu. O dia só aparece quando o
   *  intervalo cruza a meia-noite ou não é hoje: repetir a data nos dois lados de um intervalo
   *  de uma hora é ruído. */
  periodoRecuperado = computed(() => {
    const r = this.recuperacao();
    if (!r) return '';

    const de = new Date(r.de), ate = new Date(r.ate);
    const hora = (d: Date) =>
      `${String(d.getHours()).padStart(2, '0')}h${String(d.getMinutes()).padStart(2, '0')}`;

    const hoje = new Date();
    const mesmoDia = (a: Date, b: Date) => a.toDateString() === b.toDateString();
    const dia = (d: Date) =>
      mesmoDia(d, hoje) ? 'hoje'
        : mesmoDia(d, new Date(hoje.getTime() - 864e5)) ? 'ontem'
          : `${String(d.getDate()).padStart(2, '0')}/${String(d.getMonth() + 1).padStart(2, '0')}`;

    return mesmoDia(de, ate)
      ? `${dia(de)}, ${hora(de)} às ${hora(ate)}`
      : `${dia(de)} ${hora(de)} até ${dia(ate)} ${hora(ate)}`;
  });

  ngOnInit() {
    this.carregarConversas();
    this.painel.status().subscribe({
      next: s => {
        this.amareloMin.set(s.semaforoAmareloMinutos);
        this.vermelhoMin.set(s.semaforoVermelhoMinutos);
        this.janela.set(janelaDoStatus(s));
      },
      error: () => { }
    });

    // A thread cuida de si mesma quando chega mensagem; aqui só a LISTA precisa reagir.
    this.inscricoes.push(
      this.realtime.mensagemRecebida$.subscribe(() => this.mesclarTopo()),
      this.realtime.conversaAberta$.subscribe(() => this.mesclarTopo())
    );

    // Um tick por minuto: o semáforo amadurece sem nova requisição.
    this.timerAgora = setInterval(() => this.agora.set(new Date()), 60_000);
  }

  ngOnDestroy() {
    this.inscricoes.forEach(s => s.unsubscribe());
    if (this.timerAgora) clearInterval(this.timerAgora);
    if (this.buscaTimer) clearTimeout(this.buscaTimer);
  }

  // ---------------------------------------------------------------- lista (cursor)
  /** RESET: primeira página, sem cursor. Aba e busca entram por aqui. */
  carregarConversas() {
    this.cursorEm = null;
    this.cursorId = null;
    this.carregandoLista.set(true);
    this.servico.conversas(this.filtro(), this.busca().trim() || undefined, null, null, 30).subscribe({
      next: p => {
        this.conversas.set(p.itens);
        this.temMais.set(p.temMais);
        this.atualizarCursor(p.itens);
        this.carregandoLista.set(false);
        this.abrirPedidaPelaRota();
      },
      error: () => this.carregandoLista.set(false)
    });
  }

  /** `?conversa=N` — o Meu Dia e o detalhe do contato mandam o vendedor direto para a conversa.
   *
   *  ===================== POR QUE NÃO PROCURAR NA LISTA =====================
   *  Era o que fazia antes: procurava na primeira página, e se não achasse trocava o filtro para
   *  "Todas" e tentava de novo. Com 30 itens por página e uma base real, a conversa clicada no
   *  Meu Dia frequentemente está na página 4 — e a tela abria VAZIA, sem erro e sem explicação.
   *  Rolar até achar também não serve: a lista se reordena em tempo real e o alvo pode nunca
   *  aparecer.
   *
   *  Agora ela é BUSCADA PELO ID (`GET /api/conversas/{id}`) e FIXADA no topo da lista se não
   *  estiver nela. O item fixado fica marcado, para o vendedor entender por que aquela conversa
   *  está fora da ordem — e some assim que ele troca de aba ou busca.
   *  ========================================================================= */
  private abrirPedidaPelaRota() {
    const pedida = Number(this.rota.snapshot.queryParamMap.get('conversa') ?? 0);
    if (!pedida || this.consumiuRota) return;
    this.consumiuRota = true;

    const naLista = this.conversas().find(c => c.id === pedida);
    if (naLista) { this.abrir(naLista); return; }

    this.buscandoPedida.set(true);
    this.servico.conversa(pedida).subscribe({
      next: c => {
        this.buscandoPedida.set(false);
        this.fixada.set(c.id);
        // No TOPO, não na posição cronológica: quem veio de um link precisa VER a conversa que
        // pediu, e enfiá-la no meio de trinta linhas seria escondê-la de novo.
        this.conversas.update(cs => [c, ...cs.filter(x => x.id !== c.id)]);
        this.abrir(c);
      },
      error: e => {
        this.buscandoPedida.set(false);
        // Mensagem CLARA em vez de tela vazia. 404 cobre inexistente e de outro tenant.
        this.erroPedida.set(e.status === 404
          ? 'Essa conversa não existe mais, ou não é da sua empresa.'
          : 'Não foi possível abrir a conversa. Tente de novo.');
      }
    });
  }

  private consumiuRota = false;

  /** Id da conversa que veio pelo link e foi fixada no topo por não estar na lista. */
  fixada = signal<number | null>(null);
  buscandoPedida = signal(false);
  erroPedida = signal('');

  /** Trocar de aba ou buscar desfaz a fixação: a partir daí o vendedor está navegando, e uma
   *  linha presa no topo fora da ordem viraria ruído. */
  private soltarFixada() {
    this.fixada.set(null);
    this.erroPedida.set('');
  }

  /** "Carregar mais": próxima página pelo cursor, ANEXADA ao fim, com dedupe defensivo. */
  carregarMais() {
    if (this.carregandoMais() || !this.temMais()) return;
    this.carregandoMais.set(true);
    this.servico.conversas(this.filtro(), this.busca().trim() || undefined,
      this.cursorEm, this.cursorId, 30).subscribe({
      next: p => {
        const existentes = new Set(this.conversas().map(c => c.id));
        const novas = p.itens.filter(c => !existentes.has(c.id));
        this.conversas.update(cs => [...cs, ...novas]);
        this.temMais.set(p.temMais);
        this.atualizarCursor(this.conversas());
        this.carregandoMais.set(false);
      },
      error: () => this.carregandoMais.set(false)
    });
  }

  /** TEMPO REAL: recarrega só a PRIMEIRA página e a mescla no topo, PRESERVANDO a cauda já
   *  paginada. Dedupe por id.
   *
   *  É a peça que não se acerta de primeira: sem ela, ou se recarrega tudo (e o vendedor perde
   *  a rolagem e o "carregar mais") ou a lista diverge do servidor. */
  mesclarTopo() {
    this.servico.conversas(this.filtro(), this.busca().trim() || undefined, null, null, 30)
      .subscribe(p => {
        const idsFrescos = new Set(p.itens.map(c => c.id));
        const cauda = this.conversas().filter(c => !idsFrescos.has(c.id));
        const nova = [...p.itens, ...cauda];
        this.conversas.set(nova);
        // Havendo cauda (páginas extras já carregadas), o "tem mais" continua o de antes.
        this.temMais.set(cauda.length > 0 ? this.temMais() : p.temMais);
        this.atualizarCursor(nova);

        // Mantém o selecionado fresco: dono e não lidas podem ter mudado.
        //
        // ⚠️ SÓ substitui quando ACHA. O `?? atual` que havia aqui parecia inofensivo e era o
        // defeito do botão "Assumir": conversa que saiu do filtro atual (justamente o que
        // acontece ao ganhar dono, estando em "Não atribuídas") não é encontrada, e o objeto
        // velho voltava por cima do que a ação acabara de corrigir.
        const atual = this.sel();
        if (atual) {
          const fresco = nova.find(c => c.id === atual.id);
          if (fresco) this.sel.set(fresco);
        }
      });
  }

  /** Cursor = (ultimaMensagemEm, id) do ÚLTIMO item da lista. */
  private atualizarCursor(lista: ConversaResumo[]) {
    const ultimo = lista[lista.length - 1];
    this.cursorEm = ultimo?.ultimaMensagemEm ?? null;
    this.cursorId = ultimo?.id ?? null;
  }

  trocarAba(f: FiltroConversa) {
    this.filtro.set(f);
    this.sel.set(null);
    this.soltarFixada();
    this.carregarConversas();
  }

  aoBuscar(valor: string) {
    this.busca.set(valor);
    this.soltarFixada();
    if (this.buscaTimer) clearTimeout(this.buscaTimer);
    this.buscaTimer = setTimeout(() => this.carregarConversas(), 350);
  }

  abrir(c: ConversaResumo) {
    this.sel.set(c);
    this.comprasDoSelecionado.set(null);
    if (c.contatoGanhou) this.carregarCompras(c.contatoId);
  }

  // ---------------------------------------------------------------- o cliente que voltou (NEG-3)
  comprasDoSelecionado = signal<{ quantidade: number; ultimaEm: string | null } | null>(null);
  abrindoNegociacao = signal(false);

  /** ⚠️ SÓ PARA A CONVERSA ABERTA, e só para quem já comprou. Trazer a contagem junto de cada
   *  linha da lista custaria um subselect em `vendas` por conversa, em toda rolagem — para um
   *  número que aparece numa faixa de uma conversa só.
   *
   *  Falha em silêncio: a faixa já diz "cliente recorrente" com o que veio da lista, e o número
   *  é enfeite. Um erro aqui não pode tirar o botão da tela. */
  private carregarCompras(contatoId: number) {
    this.vendasApi.doContato(contatoId).subscribe({
      next: v => {
        const validas = v.filter(x => x.status !== 'cancelada');
        this.comprasDoSelecionado.set(validas.length === 0 ? null : {
          quantidade: validas.length,
          ultimaEm: validas.map(x => x.fechadaEm).sort().at(-1) ?? null
        });
      },
      error: () => this.comprasDoSelecionado.set(null)
    });
  }

  // ---------------------------------------------------------------- registrar venda daqui
  /** ===================== O ATALHO DA CAIXA =====================
   *
   *  O vendedor fecha a venda NA CONVERSA — é ali que o cliente diz "pode mandar". Antes disso
   *  ele tinha que sair da caixa, achar o contato ou o card, e voltar; no meio do caminho a venda
   *  simplesmente não era registrada, que é o modo de falha que este atalho existe para evitar.
   *
   *  ⚠️ O MESMO `app-modal-fechamento` do funil e do detalhe, e o mesmo `marcarGanho`. Uma tela
   *  de venda própria aqui seria a terceira porta para o mesmo fato — e a que esqueceria o campo
   *  de campanha na primeira mudança.
   *
   *  ⚠️ SÓ APARECE EM ATENDIMENTO. Conversa sem dono não tem quem responda pela venda: primeiro
   *  alguém assume, depois registra. É a mesma linha de corte de "Liberar".
   *
   *  ⚠️ E SÓ PARA QUEM AINDA NÃO COMPROU. Com `ganho_em` carimbado a API devolve 409 ("esta venda
   *  já está marcada como fechada"), e o caminho certo é a faixa de cliente recorrente logo
   *  acima — "Abrir nova negociação" limpa o carimbo e aí sim o botão aparece. Oferecer um botão
   *  que sempre erra é pior que não oferecer.
   *  ============================================================== */
  podeRegistrarVenda = computed(() => {
    const c = this.sel();
    return !!c && c.responsavelId !== null && !c.contatoGanhou;
  });

  fechando = signal(false);
  salvandoVenda = signal(false);
  erroVenda = signal('');
  canaisFechamento = signal<OpcaoCanal[]>([]);
  canalDetectado = signal<number | null>(null);

  abrirVenda() {
    const c = this.sel();
    if (!c || !this.podeRegistrarVenda()) return;

    this.erroVenda.set('');
    this.fechando.set(true);

    // Falha em silêncio: o canal é opcional, e derrubar o fechamento porque a lista de campanhas
    // não veio trocaria um campo a menos por uma venda não registrada.
    this.contatosApi.canaisDoFechamento(c.contatoId).subscribe({
      next: r => { this.canaisFechamento.set(r.canais); this.canalDetectado.set(r.detectadoId); },
      error: () => { this.canaisFechamento.set([]); this.canalDetectado.set(null); }
    });
  }

  cancelarVenda() {
    this.fechando.set(false);
    this.erroVenda.set('');
    this.canaisFechamento.set([]);
    this.canalDetectado.set(null);
  }

  confirmarVenda(r: ResultadoFechamento) {
    const c = this.sel();
    if (!c) return;

    this.salvandoVenda.set(true);
    this.erroVenda.set('');

    this.contatosApi.marcarGanho(c.contatoId, r.valor, r.canalId).subscribe({
      next: () => {
        this.salvandoVenda.set(false);
        this.cancelarVenda();
        this.toast.sucesso(`Venda de ${c.contatoNome} registrada.`);
        // Recarrega do SERVIDOR: a etapa mudou, `contatoGanhou` mudou, e com prazo zero a venda
        // já nasce concluída e a conversa volta para a fila. Nada disso dá para adivinhar aqui.
        this.mesclarTopo();
      },
      error: e => {
        this.salvandoVenda.set(false);
        this.erroVenda.set(e.error?.erro ?? 'Não foi possível registrar a venda.');
      }
    });
  }

  /** O `reabrir` QUE JÁ EXISTE (NEG-1): move para a primeira etapa, limpa o carimbo e preserva o
   *  histórico de vendas. Nada de endpoint novo — seria uma segunda porta para o mesmo fato. */
  abrirNovaNegociacao() {
    const c = this.sel();
    if (!c || this.abrindoNegociacao()) return;

    this.abrindoNegociacao.set(true);
    this.contatosApi.reabrir(c.contatoId).subscribe({
      next: () => {
        this.abrindoNegociacao.set(false);
        this.toast.sucesso('Negociação aberta. O contato voltou para a primeira etapa do funil.');
        // A faixa some porque `contatoGanhou` deixou de valer — e o dado vem do servidor, não de
        // um `set` local: quem decide o estado do contato é ele.
        this.mesclarTopo();
      },
      error: e => {
        this.abrindoNegociacao.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível abrir a negociação.');
      }
    });
  }

  // ---------------------------------------------------------------- atribuição
  /** ===================== A ETIQUETA QUE DIZIA "VENDA" DEPOIS DE ENTREGUE =====================
   *
   *  A caixa mostrava `etapaNome` cru. Para quem comprou e RECEBEU, isso dizia "Venda" — e
   *  apontava para uma coluna do funil onde o card não está: o NEG-2 tira da coluna Venda quem
   *  não tem pedido em aberto, senão ela acumula para sempre.
   *
   *  Duas telas do mesmo produto discordando sobre o mesmo contato, e a caixa era a que mentia:
   *  "Venda" lê como negócio acontecendo, e o vendedor abriria a conversa esperando um pedido a
   *  caminho.
   *
   *  ⚠️ NÃO É `contatoGanhou` SOZINHO. Quem comprou e tem OUTRO pedido a caminho continua sendo
   *  "Venda" — é exatamente a situação que o card do kanban também mostra. O que muda o rótulo é
   *  não haver mais nada em aberto.
   *  ======================================================================== */
  rotuloEtapa(c: ConversaResumo): string {
    return c.contatoGanhou && c.vendasEmAberto === 0 ? 'Pedido concluído' : c.etapaNome;
  }

  ehMinha(c: ConversaResumo | null): boolean { return !!c && c.responsavelId === this.meuId(); }

  donoLabel(c: ConversaResumo): string {
    if (c.responsavelId === null) return 'Aguardando';
    return this.ehMinha(c) ? 'Você' : (c.responsavelNome ?? 'Outro');
  }

  assumir() {
    const c = this.sel();
    if (!c) return;
    this.servico.assumir(c.id).subscribe({
      next: () => {
        this.toast.sucesso('Conversa atribuída a você.');
        this.aplicarDono(c.id, this.meuId(), this.auth.usuario()?.nome ?? null);
        this.mesclarTopo();
      },
      // 409 = já é de outro vendedor. A mensagem da API já explica.
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível assumir.')
    });
  }

  liberar() {
    const c = this.sel();
    if (!c) return;
    this.servico.liberar(c.id).subscribe({
      next: () => {
        this.toast.info('Conversa devolvida para não atribuídas.');
        this.aplicarDono(c.id, null, null);
        this.mesclarTopo();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível liberar.')
    });
  }

  /** ===================== O RESULTADO É APLICADO NA HORA =====================
   *  Assumir só aparecia depois de recarregar a página, e a causa era o `?? atual` do
   *  `mesclarTopo`: ele repesca o selecionado na lista recém-baixada e, NÃO ACHANDO, mantém o
   *  objeto velho — com `responsavelId` nulo, ou seja, com o botão "Assumir" ainda na tela.
   *
   *  E não achar é o caso NORMAL da ação: quem assume costuma estar na aba "Não atribuídas", de
   *  onde a conversa sai no exato instante em que ganha dono. O recarregamento consertava porque
   *  a lista inteira vinha do zero.
   *
   *  A API já confirmou a mudança quando isto roda — não é palpite otimista, é o resultado
   *  conhecido sendo refletido sem esperar uma segunda ida ao servidor. O `mesclarTopo` continua
   *  logo atrás para trazer o resto (posição, não lidas, prévia). */
  private aplicarDono(conversaId: number, responsavelId: number | null, responsavelNome: string | null) {
    const aplicar = (c: ConversaResumo): ConversaResumo =>
      c.id === conversaId ? { ...c, responsavelId, responsavelNome } : c;

    this.conversas.update(lista => lista.map(aplicar));

    const atual = this.sel();
    if (atual?.id === conversaId) this.sel.set(aplicar(atual));
  }

  // ---------------------------------------------------------------- semáforo
  /** A COR É CALCULADA AQUI, no cliente, a partir do timestamp — nunca pedida ao servidor:
   *  ela muda com o passar do tempo e a lista precisa envelhecer entre requisições. */
  urgencia(c: ConversaResumo): Urgencia {
    return urgenciaDe(
      c.aguardandoDesde, this.amareloMin(), this.vermelhoMin(), this.agora(), this.janela());
  }

  espera(c: ConversaResumo): string {
    return rotuloEspera(c.aguardandoDesde, this.agora());
  }

  iniciais(nome: string): string {
    const p = (nome || '').trim().split(/\s+/);
    return ((p[0]?.[0] ?? '') + (p.length > 1 ? p[p.length - 1][0] : '')).toUpperCase() || '?';
  }
}
