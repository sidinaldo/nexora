import {
  Component, ElementRef, OnDestroy, OnInit, ViewChild, computed, inject, signal
} from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { MeuDiaServico } from '../../nucleo/servicos/meu-dia.servico';
import { PainelServico } from '../../nucleo/servicos/painel.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { AcaoDoDia, ContagemDoDia } from '../../nucleo/modelos';
import { POR_PAGINA, Paginacao, rolarParaTopoDaTabela } from '../../nucleo/paginacao/paginacao';
import {
  JANELA_PADRAO, JanelaAtendimento, Urgencia, dentroDaJanela, janelaDoStatus, urgenciaDe
} from '../../nucleo/semaforo';
import { iniciais } from '../../nucleo/iniciais';
import { erroAo } from '../../nucleo/erros';

/** O recorte da lista do dia.
 *
 *  `atrasadas` corta por URGÊNCIA; os outros, por tipo de trabalho. São eixos diferentes de
 *  propósito: "por onde começo num dia ruim" é uma pergunta, "vou sentar para responder gente"
 *  é outra. */
export type FiltroDoDia = 'todas' | 'responder' | 'lembrete' | 'atrasadas';

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
  imports: [Paginacao],
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

  // ⚠️ A ORDEM DO DIA VEM DO SERVIDOR (AUD-XX). A tela ordenava pelo `momento` — conversa pela
  // espera, lembrete pela hora marcada — sobre a lista inteira. Agora `PaginaDoDia` chega
  // ordenada e já paginada; ver `ServicoMeuDia.PaginaAsync`.

  // ================================================================ filtro e página
  /** ===================== POR QUE O MEU DIA PRECISOU DE FILTRO =====================
   *  A tela nasceu para uma lista curta — "o que fazer hoje" cabia numa olhada. Com base de
   *  verdade ela abriu com 100 ações, e aí ela deixa de ser um plano e vira um backlog: o
   *  vendedor rola, perde a posição e não sabe por onde começar.
   *
   *  O filtro é por TIPO de trabalho, não por status, porque é assim que a pessoa decide o que
   *  fazer: ou ela senta para responder gente esperando, ou ela senta para tocar follow-up. São
   *  dois modos de atenção diferentes.
   *
   *  "Atrasados" é o terceiro, e é o que responde "por onde começo" num dia ruim.
   *  ================================================================================ */
  filtro = signal<FiltroDoDia>('todas');
  pagina = signal(1);

  readonly filtros: { chave: FiltroDoDia; rotulo: string }[] = [
    { chave: 'todas', rotulo: 'Tudo' },
    { chave: 'responder', rotulo: 'Esperando resposta' },
    { chave: 'lembrete', rotulo: 'Follow-ups' },
    { chave: 'atrasadas', rotulo: 'Atrasados' }
  ];

  // ===================== OS NÚMEROS DA TELA SÃO DO SERVIDOR (AUD-XX) =====================
  // Eram contados aqui sobre a lista cortada em 200: com 340 pendências, o topo dizia "200
  // ações" e o aviso logo abaixo dizia "de 340". Agora a tela pede UMA página de UMA aba e
  // recebe as contagens de todas, contadas no banco.
  // =====================================================================================
  contagens = signal<ContagemDoDia>({ todas: 0, responder: 0, lembrete: 0, atrasadas: 0 });
  totalNaAba = signal(0);
  totalPaginas = signal(1);

  /** O número da pílula de cada aba: uma LEITURA da contagem do servidor, não uma conta. */
  quantasNoFiltro(f: FiltroDoDia): number {
    return this.contagens()[f];
  }

  @ViewChild('listaTopo') private listaTopo?: ElementRef<HTMLElement>;

  /** Trocar o filtro volta para a página 1: manter a página 4 com outro recorte mostraria uma
   *  lista vazia com trabalho existindo nas páginas anteriores. */
  trocarFiltro(f: FiltroDoDia) {
    if (this.filtro() === f) return;
    this.filtro.set(f);
    this.pagina.set(1);
    this.carregar(false);
  }

  irPara(p: number) {
    this.pagina.set(p);
    this.carregar(false);
    rolarParaTopoDaTabela(this.listaTopo?.nativeElement);
  }

  /** Os contadores do topo contam o DIA INTEIRO, não a página nem a aba: "100 ações para hoje" é
   *  o tamanho do dia, e mudar esse número ao trocar de aba faria a pessoa achar que o trabalho
   *  sumiu. Leituras de `contagens`, que o servidor manda em toda página. */
  quantasConversas = computed(() => this.contagens().responder);
  quantosLembretes = computed(() => this.contagens().lembrete);
  total = computed(() => this.contagens().todas);
  vazio = computed(() => !this.carregando() && this.total() === 0);

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
    this.servico.pagina(this.filtro(), this.pagina(), POR_PAGINA).subscribe({
      next: p => {
        // Página que deixou de existir — concluiu-se o último item dela: volta para a última que
        // existe, pelo `totalPaginas` do servidor.
        if (p.itens.length === 0 && p.totalCount > 0 && this.pagina() > p.totalPaginas) {
          this.pagina.set(p.totalPaginas);
          this.carregar(false);
          return;
        }

        this.acoes.set(p.itens);
        this.contagens.set(p.contagens);
        this.totalNaAba.set(p.totalCount);
        this.totalPaginas.set(p.totalPaginas);
        // O que saiu da lista no servidor não está mais animando.
        const vivos = new Set(p.itens.map(a => this.chave(a)));
        this.saindo.update(s => new Set([...s].filter(k => vivos.has(k))));
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

    // ⚠️ OS NÚMEROS NÃO DESCEM AQUI (AUD-XX). O item anima saindo na hora; as contagens e a página
    // vêm do servidor, recarregadas depois que ele confirma — a tela não subtrai nada.
    this.servico.concluir(a.id).subscribe({
      next: () => {
        setTimeout(() => this.carregar(false), MeuDia.MsSaida);
      },
      error: e => {
        this.desmarcarSaindo(chave);
        this.toast.erro(erroAo(e, 'concluir o lembrete'));
      }
    });
  }

  private marcarSaindo(chave: string) {
    this.saindo.update(s => new Set(s).add(chave));
  }

  private desmarcarSaindo(chave: string) {
    this.saindo.update(s => { const n = new Set(s); n.delete(chave); return n; });
  }

  estaSaindo(a: AcaoDoDia): boolean { return this.saindo().has(this.chave(a)); }

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

    // ⚠️ OS DIAS VÊM DO SERVIDOR (AUD-XX). A tela dividia as horas por 12, e o dia útil é o da
    // janela da empresa — das 8h às 18h são 10 horas. Aqui só se escolhe a unidade.
    const dias = a.esperaDiasUteis ?? 0;
    if (dias >= 1) return `${dias} dia${dias > 1 ? 's' : ''}`;

    const m = a.minutosUteis;
    if (m < 1) return 'agora';
    if (m < 60) return `${m} min`;
    return `${Math.floor(m / 60)}h`;
  }

  /** Uma copia so, em `nucleo/iniciais.ts` — o avatar e a MESMA coisa em toda tela. Eram seis
   *  copias, e as de contato mostravam "(9" para quem nasceu com o telefone por nome. */
  protected readonly iniciais = iniciais;

  /** Saudação pela hora, para a tela abrir falando com a pessoa. */
  saudacao(): string {
    const h = this.agora().getHours();
    if (h < 12) return 'Bom dia';
    return h < 18 ? 'Boa tarde' : 'Boa noite';
  }
}
