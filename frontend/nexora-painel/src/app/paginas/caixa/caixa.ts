import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { CaixaServico } from '../../nucleo/servicos/caixa.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
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
  imports: [DatePipe, RouterLink, Thread],
  templateUrl: './caixa.html',
  styleUrl: './caixa.css'
})
export class Caixa implements OnInit, OnDestroy {
  private servico = inject(CaixaServico);
  private painel = inject(PainelServico);
  private auth = inject(AuthServico);
  private toast = inject(ToastServico);
  private rota = inject(ActivatedRoute);
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

        // Mantém o selecionado fresco: dono e não-lidas podem ter mudado.
        const atual = this.sel();
        if (atual) this.sel.set(nova.find(c => c.id === atual.id) ?? atual);
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

  abrir(c: ConversaResumo) { this.sel.set(c); }

  // ---------------------------------------------------------------- atribuição
  ehMinha(c: ConversaResumo | null): boolean { return !!c && c.responsavelId === this.meuId(); }

  donoLabel(c: ConversaResumo): string {
    if (c.responsavelId === null) return 'Aguardando';
    return this.ehMinha(c) ? 'Você' : (c.responsavelNome ?? 'Outro');
  }

  assumir() {
    const c = this.sel();
    if (!c) return;
    this.servico.assumir(c.id).subscribe({
      next: () => { this.toast.sucesso('Conversa atribuída a você.'); this.mesclarTopo(); },
      // 409 = já é de outro vendedor. A mensagem da API já explica.
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível assumir.')
    });
  }

  liberar() {
    const c = this.sel();
    if (!c) return;
    this.servico.liberar(c.id).subscribe({
      next: () => { this.toast.info('Conversa devolvida para não atribuídas.'); this.mesclarTopo(); },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível liberar.')
    });
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
