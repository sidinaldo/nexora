import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { MeuDiaServico } from '../../nucleo/servicos/meu-dia.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { AcaoDoDia } from '../../nucleo/modelos';
import {
  JANELA_PADRAO, JanelaAtendimento, Urgencia, dentroDaJanela, janelaDoStatus, urgenciaDe
} from '../../nucleo/semaforo';

/** O MEU DIA — a tela principal do produto.
 *
 *  ===================== POR QUE É UMA LISTA SÓ =====================
 *  A API devolve a união de duas fontes: conversas esperando resposta e lembretes vencidos. A
 *  tela NÃO as separa, e isso não é economia de espaço.
 *
 *  Separado em "Esperando resposta" e "Follow-ups", o vendedor tem que ler duas listas e decidir
 *  qual atacar primeiro — ou seja, tem que fazer na cabeça o trabalho que a tela existe para
 *  fazer. Junto e ordenado por urgência, a resposta é a primeira linha.
 *
 *  De onde o item veio é detalhe de implementação. O que ele precisa saber é o que fazer agora.
 *  ==================================================================
 *
 *  Funil e caixa de entrada todo concorrente tem. Plano de ação diário, quase nenhum comunica —
 *  é o diferencial, e por isso a tela é tratada como principal. */
@Component({
  selector: 'app-meu-dia',
  imports: [],
  templateUrl: './meu-dia.html',
  styleUrl: './meu-dia.css'
})
export class MeuDia implements OnInit, OnDestroy {
  private servico = inject(MeuDiaServico);
  private painel = inject(PainelServico);
  private realtime = inject(RealtimeServico);
  private toast = inject(ToastServico);
  private router = inject(Router);

  /** Duração da saída do item. Curta de propósito: é confirmação visual, não espetáculo — um
   *  vendedor que conclui dez lembretes seguidos não pode esperar meio segundo por cada. */
  private static readonly MsSaida = 220;

  acoes = signal<AcaoDoDia[]>([]);
  carregando = signal(true);
  erro = signal('');

  /** Itens em animação de saída: continuam renderizados, já não contam. */
  saindo = signal<Set<string>>(new Set());

  /** Concluídos localmente. Um recarregamento em corrida com a animação traria o item de volta;
   *  esta lista o mantém fora até o servidor concordar. */
  private concluidos = new Set<string>();

  amareloMin = signal(60);
  vermelhoMin = signal(240);
  janela = signal<JanelaAtendimento>(JANELA_PADRAO);

  /** Tick de relógio: a cor amadurece sozinha, sem novo fetch. */
  private agora = signal(new Date());
  private buscadoEm = Date.now();

  private timer: ReturnType<typeof setInterval> | null = null;
  private inscricoes: Subscription[] = [];

  /** Fora do expediente nada acende. O vendedor que abre o sistema às 22h não precisa ver a
   *  lista inteira em vermelho por algo que ninguém poderia ter respondido. */
  expedienteAberto = computed(() => dentroDaJanela(this.agora(), this.janela()));

  /** A LISTA ÚNICA, ordenada pelo momento em que cada coisa deveria acontecer.
   *
   *  Ordenação ascendente por `momento`: o que já passou da hora sobe. Uma conversa esperando
   *  desde ontem às 23h vem antes de um lembrete marcado para hoje às 9h, porque espera mais
   *  tempo — e um lembrete de ontem vem antes dos dois. */
  visiveis = computed(() =>
    this.acoes()
      .filter(a => !this.concluidos.has(this.chave(a)))
      .slice()
      .sort((x, y) => this.momento(x) - this.momento(y)));

  /** Contadores derivados da lista LOCAL, não do payload: depois de concluir um item de forma
   *  otimista, o número no topo tem que cair junto. */
  quantasConversas = computed(() => this.ativos().filter(a => a.tipo === 'responder').length);
  quantosLembretes = computed(() => this.ativos().filter(a => a.tipo === 'lembrete').length);
  total = computed(() => this.ativos().length);
  vazio = computed(() => !this.carregando() && this.total() === 0);

  private ativos = computed(() => {
    const saindo = this.saindo();
    return this.visiveis().filter(a => !saindo.has(this.chave(a)));
  });

  ngOnInit() {
    this.carregar();

    this.painel.status().subscribe({
      next: s => {
        this.amareloMin.set(s.semaforoAmareloMinutos);
        this.vermelhoMin.set(s.semaforoVermelhoMinutos);
        this.janela.set(janelaDoStatus(s));
      },
      error: () => { }
    });

    this.inscricoes.push(
      // Mensagem nova muda quem está esperando: o item do contato pode entrar, sair ou mudar de
      // posição. Recarrega em silêncio — a lista se reordena sozinha pelo `momento`.
      this.realtime.mensagemRecebida$.subscribe(() => this.carregar(false)),
      this.realtime.conversaAberta$.subscribe(() => this.carregar(false))
    );

    this.timer = setInterval(() => this.agora.set(new Date()), 30_000);
  }

  ngOnDestroy() {
    if (this.timer) clearInterval(this.timer);
    this.inscricoes.forEach(i => i.unsubscribe());
  }

  carregar(comSpinner = true) {
    if (comSpinner) this.carregando.set(true);
    this.servico.meuDia().subscribe({
      next: d => {
        // O servidor é a verdade sobre o que ainda está pendente: id que sumiu do payload já foi
        // resolvido de fato, e sai da lista local de concluídos.
        const vivos = new Set(d.acoes.map(a => this.chave(a)));
        this.concluidos.forEach(k => { if (!vivos.has(k)) this.concluidos.delete(k); });

        this.acoes.set(d.acoes);
        this.buscadoEm = Date.now();
        this.carregando.set(false);
        this.erro.set('');
      },
      error: () => {
        this.erro.set('Não foi possível carregar o seu dia.');
        this.carregando.set(false);
      }
    });
  }

  // ================================================================ ações
  /** Clicar abre o CONTEXTO da ação, não uma tela genérica: conversa vai para a thread na caixa
   *  de entrada, lembrete vai para o detalhe do contato — que é onde estão os dados, o histórico
   *  e os outros lembretes dele. */
  abrir(a: AcaoDoDia) {
    if (a.tipo === 'responder' && a.conversaId) {
      // A caixa BUSCA a conversa pelo id e a fixa no topo se não estiver na página carregada —
      // antes ela só procurava na primeira página e a tela abria vazia. Ver `abrirPedidaPelaRota`.
      this.router.navigate(['/caixa'], { queryParams: { conversa: a.conversaId } });
      return;
    }

    // Lembrete: o detalhe do contato, com o lembrete EM FOCO. Sem o parâmetro, quem clica cai
    // numa tela com cinco lembretes e precisa reencontrar o que estava fazendo.
    //
    // `a.id` É o id do lembrete quando `tipo === 'lembrete'` — não há campo separado; a chave da
    // lista é o par (tipo, id) justamente porque um lembrete e uma conversa podem colidir no
    // número.
    this.router.navigate(['/contatos', a.contatoId], {
      queryParams: a.tipo === 'lembrete' ? { lembrete: a.id } : undefined
    });
  }

  /** Conclui o lembrete OTIMISTA: some da lista na hora, com a animação, e a chamada vai em
   *  paralelo. Se a API recusar, o item volta e o toast diz por quê — sem isso, concluir teria
   *  meio segundo de latência e o vendedor sentiria. */
  concluir(a: AcaoDoDia, evento: Event) {
    evento.stopPropagation();
    const chave = this.chave(a);
    if (this.saindo().has(chave)) return;

    this.marcarSaindo(chave);

    this.servico.concluir(a.id).subscribe({
      next: () => {
        this.concluidos.add(chave);
        setTimeout(() => this.removerLocal(chave), MeuDia.MsSaida);
      },
      error: e => {
        this.desmarcarSaindo(chave);
        this.toast.erro(e.error?.erro ?? 'Não foi possível concluir.');
      }
    });
  }

  private marcarSaindo(chave: string) {
    this.saindo.update(s => new Set(s).add(chave));
  }

  private desmarcarSaindo(chave: string) {
    this.saindo.update(s => { const n = new Set(s); n.delete(chave); return n; });
  }

  private removerLocal(chave: string) {
    this.acoes.update(lista => lista.filter(a => this.chave(a) !== chave));
    this.desmarcarSaindo(chave);
  }

  estaSaindo(a: AcaoDoDia): boolean { return this.saindo().has(this.chave(a)); }

  // ================================================================ ordenação
  /** A chave de ordenação: o instante em que a ação deveria acontecer.
   *
   *  • lembrete com hora  -> data-alvo naquela hora
   *  • lembrete sem hora  -> fim da data-alvo (é "em algum momento do dia", então depois dos
   *                          que têm horário marcado)
   *  • conversa           -> quando o cliente começou a esperar
   *
   *  Como lembrete atrasado tem data-alvo no passado, ele sobe naturalmente — sem regra
   *  especial de "atrasados primeiro". */
  private momento(a: AcaoDoDia): number {
    if (a.tipo === 'lembrete' && a.dataAlvo) {
      const [ano, mes, dia] = a.dataAlvo.substring(0, 10).split('-').map(Number);
      if (a.horaAlvo) {
        const [h, m] = a.horaAlvo.substring(0, 5).split(':').map(Number);
        return new Date(ano, mes - 1, dia, h, m).getTime();
      }
      return new Date(ano, mes - 1, dia, 23, 59).getTime();
    }
    return a.aguardandoDesde ? new Date(a.aguardandoDesde).getTime() : Number.MAX_SAFE_INTEGER;
  }

  /** Tipo + id: um lembrete e uma conversa podem ter o mesmo id numérico (são tabelas
   *  diferentes), e sem o prefixo concluir um removeria o outro da tela. */
  private chave(a: AcaoDoDia): string { return `${a.tipo}:${a.id}`; }

  // ================================================================ apresentação
  /** O que fazer, em uma frase. Para conversa é sempre responder; para lembrete é o título que
   *  a própria pessoa escreveu ("Ligar para confirmar", "Enviar proposta"). */
  oQueFazer(a: AcaoDoDia): string {
    return a.tipo === 'responder' ? `Responder ${a.contatoNome}` : a.titulo;
  }

  /** O horário que a linha exibe. Lembrete mostra a hora marcada; conversa mostra desde quando
   *  o cliente espera — não há "horário sugerido" para conversa na API (ver o relatório). */
  quando(a: AcaoDoDia): string {
    if (a.tipo === 'lembrete') {
      if (a.atrasado) return 'atrasado';
      return a.horaAlvo ? a.horaAlvo.substring(0, 5) : 'hoje';
    }
    if (!a.aguardandoDesde) return '';
    const d = new Date(a.aguardandoDesde);
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  }

  /** A COR SAI DAQUI, no cliente, a partir dos minutos ÚTEIS que o servidor calculou — nunca
   *  pedida à API: ela muda com o tempo, e a lista precisa envelhecer entre requisições.
   *
   *  Só conversa tem semáforo. Lembrete tem hora marcada, que é outra coisa. */
  urgencia(a: AcaoDoDia): Urgencia {
    if (a.tipo !== 'responder') return 'baixa';
    if (!this.expedienteAberto()) return 'fora';

    // Espera acima da janela medível: o número não veio, mas a URGÊNCIA é certa. Alguém
    // esperando há mais de 30 dias é o caso mais vermelho que existe — devolver 'baixa' aqui
    // (o que acontecia quando `minutosUteis` era nulo) apagaria justamente o pior item da lista.
    if (a.esperaAcimaDaJanela) return 'alta';
    if (a.minutosUteis == null) return 'baixa';

    // Minutos do servidor + o que passou desde o fetch. Não recalculamos a espera inteira aqui:
    // o navegador não tem os feriados da empresa, e o número passaria a divergir do servidor.
    const desdeOFetch = Math.floor((this.agora().getTime() - this.buscadoEm) / 60000);
    const total = a.minutosUteis + Math.max(0, desdeOFetch);

    if (total >= this.vermelhoMin()) return 'alta';
    if (total >= this.amareloMin()) return 'media';
    return 'baixa';
  }

  espera(a: AcaoDoDia): string {
    // O servidor não mede espera acima da janela de feriados carregada: o número sairia sem
    // descontar feriados antigos, maior que o real e com cara de exato. "mais de 30 dias" é
    // verdade; "12.480 min" não seria, e alguém acreditaria.
    if (a.esperaAcimaDaJanela) return 'mais de 30 dias';
    if (a.minutosUteis == null) return '';
    const m = a.minutosUteis;
    if (m < 1) return 'agora';
    if (m < 60) return `${m} min`;
    const h = Math.floor(m / 60);
    return h < 12 ? `${h}h` : `${Math.floor(h / 12)} dia${h >= 24 ? 's' : ''}`;
  }

  iniciais(nome: string): string {
    const p = (nome || '').trim().split(/\s+/);
    return ((p[0]?.[0] ?? '') + (p.length > 1 ? p[p.length - 1][0] : '')).toUpperCase() || '?';
  }

  /** Saudação pela hora, para a tela abrir falando com a pessoa. */
  saudacao(): string {
    const h = this.agora().getHours();
    if (h < 12) return 'Bom dia';
    return h < 18 ? 'Boa tarde' : 'Boa noite';
  }
}
